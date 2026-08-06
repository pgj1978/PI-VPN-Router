using System.Globalization;

namespace PiRouter.Core.Services;

public sealed record WireGuardPeer(
    string PublicKey,
    string? Endpoint,
    string AllowedIps,
    DateTimeOffset? LatestHandshake,
    long BytesReceived,
    long BytesSent)
{
    public TimeSpan? HandshakeAge =>
        LatestHandshake is null ? null : DateTimeOffset.UtcNow - LatestHandshake.Value;
}

public sealed record WireGuardStatus(
    bool Up,
    string InterfaceName,
    int? ListenPort,
    int? FwMark,
    IReadOnlyList<WireGuardPeer> Peers)
{
    public static WireGuardStatus Down(string name) => new(false, name, null, null, []);

    public WireGuardPeer? PrimaryPeer => Peers.FirstOrDefault();

    /// <summary>
    /// Routing table wg-quick allocated. It reuses the fwmark value as the table number,
    /// so discovering the mark discovers the table.
    /// </summary>
    public int? TableId => FwMark;

    public long TotalReceived => Peers.Sum(p => p.BytesReceived);
    public long TotalSent => Peers.Sum(p => p.BytesSent);

    /// <summary>True when a handshake happened recently enough for the tunnel to be usable.</summary>
    public bool IsHealthy(TimeSpan maxHandshakeAge) =>
        Up && PrimaryPeer?.HandshakeAge is { } age && age <= maxHandshakeAge;

    /// <summary>
    /// Parses `wg show &lt;iface&gt; dump`, which is tab-separated and stable, rather than the
    /// human-readable output the previous implementation scraped.
    ///
    /// Line 1 is the interface: privkey, pubkey, listen-port, fwmark.
    /// Lines 2+ are peers: pubkey, psk, endpoint, allowed-ips, handshake(unix), rx, tx, keepalive.
    /// </summary>
    public static WireGuardStatus ParseDump(string interfaceName, string dump)
    {
        var lines = dump.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return Down(interfaceName);

        var header = lines[0].Split('\t');
        var listenPort = header.Length > 2 ? ParseInt(header[2]) : null;
        var fwMark = header.Length > 3 ? ParseMark(header[3]) : null;

        var peers = new List<WireGuardPeer>();
        foreach (var line in lines.Skip(1))
        {
            var f = line.Split('\t');
            if (f.Length < 8) continue;

            peers.Add(new WireGuardPeer(
                PublicKey: f[0],
                Endpoint: f[2] is "(none)" or "" ? null : f[2],
                AllowedIps: f[3],
                LatestHandshake: ParseHandshake(f[4]),
                BytesReceived: ParseLong(f[5]),
                BytesSent: ParseLong(f[6])));
        }

        return new WireGuardStatus(true, interfaceName, listenPort, fwMark, peers);
    }

    private static DateTimeOffset? ParseHandshake(string value) =>
        long.TryParse(value, CultureInfo.InvariantCulture, out var unix) && unix > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : null;

    private static long ParseLong(string value) =>
        long.TryParse(value, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static int? ParseInt(string value) =>
        int.TryParse(value, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>fwmark is printed as hex ("0xca6d"), decimal, or "off" when unset.</summary>
    private static int? ParseMark(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("off", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToInt32(value[2..], 16)
                : int.TryParse(value, CultureInfo.InvariantCulture, out var v) ? v : null;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            return null;
        }
    }
}
