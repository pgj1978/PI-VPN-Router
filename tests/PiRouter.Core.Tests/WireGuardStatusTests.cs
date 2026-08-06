using PiRouter.Core.Services;

namespace PiRouter.Core.Tests;

/// <summary>
/// Parsing is driven off `wg show &lt;iface&gt; dump`, a stable tab-separated format, rather
/// than the human-readable output the previous implementation scraped with regexes.
/// </summary>
public class WireGuardStatusTests
{
    // Interface line: privkey, pubkey, listen-port, fwmark
    // Peer line:      pubkey, psk, endpoint, allowed-ips, handshake, rx, tx, keepalive
    private const string ConnectedDump =
        "cHJpdmF0ZQ==\tcHVibGlj\t51820\t0xca6d\n" +
        "QWhiKTxKWp9wo3blPDcMdA1Y/Vn69u2d8WQKQMTuoWw=\t(none)\t185.44.76.188:51820\t0.0.0.0/0\t1785000000\t434288\t43008\t25\n";

    [Fact]
    public void Parses_a_connected_tunnel()
    {
        var status = WireGuardStatus.ParseDump("wg0", ConnectedDump);

        Assert.True(status.Up);
        Assert.Equal("wg0", status.InterfaceName);
        Assert.Equal(51820, status.ListenPort);
        Assert.Single(status.Peers);
        Assert.Equal("185.44.76.188:51820", status.PrimaryPeer!.Endpoint);
        Assert.Equal(434288, status.TotalReceived);
        Assert.Equal(43008, status.TotalSent);
    }

    [Fact]
    public void Discovers_the_routing_table_from_the_hex_fwmark()
    {
        // 0xca6d == 51821, and wg-quick reuses the mark as the table number. Getting this
        // wrong means the bypass rule is installed relative to the wrong table.
        var status = WireGuardStatus.ParseDump("wg0", ConnectedDump);

        Assert.Equal(51821, status.FwMark);
        Assert.Equal(51821, status.TableId);
    }

    [Fact]
    public void Handles_a_tunnel_with_no_fwmark()
    {
        var status = WireGuardStatus.ParseDump("wg0", "priv\tpub\t51820\toff\n");

        Assert.True(status.Up);
        Assert.Null(status.FwMark);
        Assert.Null(status.TableId);
    }

    [Fact]
    public void A_peer_that_has_never_handshaken_has_no_timestamp()
    {
        var status = WireGuardStatus.ParseDump("wg0",
            "priv\tpub\t51820\t0xca6d\npeerkey\t(none)\t1.2.3.4:51820\t0.0.0.0/0\t0\t0\t0\t25\n");

        Assert.Null(status.PrimaryPeer!.LatestHandshake);
        Assert.Null(status.PrimaryPeer.HandshakeAge);
    }

    [Fact]
    public void An_endpointless_peer_reports_no_endpoint()
    {
        var status = WireGuardStatus.ParseDump("wg0",
            "priv\tpub\t51820\t0xca6d\npeerkey\t(none)\t(none)\t0.0.0.0/0\t0\t0\t0\t25\n");

        Assert.Null(status.PrimaryPeer!.Endpoint);
    }

    [Fact]
    public void Empty_output_means_the_tunnel_is_down()
    {
        var status = WireGuardStatus.ParseDump("wg0", "");

        Assert.False(status.Up);
        Assert.Empty(status.Peers);
    }

    [Fact]
    public void Health_depends_on_a_recent_handshake()
    {
        var recent = Dump(DateTimeOffset.UtcNow.AddSeconds(-30));
        var stale = Dump(DateTimeOffset.UtcNow.AddMinutes(-10));

        Assert.True(WireGuardStatus.ParseDump("wg0", recent).IsHealthy(TimeSpan.FromMinutes(3)));
        Assert.False(WireGuardStatus.ParseDump("wg0", stale).IsHealthy(TimeSpan.FromMinutes(3)));
    }

    [Fact]
    public void A_downed_tunnel_is_never_healthy() =>
        Assert.False(WireGuardStatus.Down("wg0").IsHealthy(TimeSpan.FromMinutes(3)));

    [Fact]
    public void Malformed_peer_lines_are_skipped_rather_than_throwing()
    {
        var status = WireGuardStatus.ParseDump("wg0", "priv\tpub\t51820\t0xca6d\ngarbage\n");

        Assert.True(status.Up);
        Assert.Empty(status.Peers);
    }

    private static string Dump(DateTimeOffset handshake) =>
        $"priv\tpub\t51820\t0xca6d\npeerkey\t(none)\t1.2.3.4:51820\t0.0.0.0/0\t{handshake.ToUnixTimeSeconds()}\t100\t100\t25\n";
}
