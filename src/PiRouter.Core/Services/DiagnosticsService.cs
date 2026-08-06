using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiRouter.Core.Configuration;
using PiRouter.Core.Process;

namespace PiRouter.Core.Services;

public enum CheckStatus { Pass, Warn, Fail }

public sealed record DiagnosticCheck(
    string Id,
    string Name,
    CheckStatus Status,
    string Detail,
    string? Remediation = null);

public sealed record DiagnosticsReport(
    DateTimeOffset RanAt,
    IReadOnlyList<DiagnosticCheck> Checks)
{
    public CheckStatus Overall =>
        Checks.Any(c => c.Status == CheckStatus.Fail) ? CheckStatus.Fail
        : Checks.Any(c => c.Status == CheckStatus.Warn) ? CheckStatus.Warn
        : CheckStatus.Pass;
}

public interface IDiagnosticsService
{
    Task<DiagnosticsReport> RunAsync(CancellationToken ct = default);
}

/// <summary>
/// Health checks aimed squarely at the failures this router has actually suffered.
///
/// The tunnel once went down because the host's /etc/resolv.conf pointed at a resolver that
/// had stopped answering, and then stayed down after the host was fixed because the API
/// container still held the stale resolv.conf it had been started with. Both of those are
/// checks here, so the same outage would be one glance at a page rather than a long dig.
/// </summary>
public sealed class DiagnosticsService(
    IProcessRunner runner,
    IVpnService vpn,
    IConfigStore config,
    INetworkDiscovery discovery,
    IDnsmasqService dnsmasq,
    IReconciler reconciler,
    IOptions<RouterOptions> options,
    ILogger<DiagnosticsService> logger) : IDiagnosticsService
{
    private readonly RouterOptions _options = options.Value;

    public async Task<DiagnosticsReport> RunAsync(CancellationToken ct = default)
    {
        var checks = new List<DiagnosticCheck>();

        checks.Add(await CheckIpForwardingAsync(ct));
        checks.Add(await CheckInterfaceAsync(_options.LanInterface, "lan-interface", "LAN interface", ct));
        checks.Add(await CheckInterfaceAsync(_options.WanInterface, "wan-interface", "WAN interface", ct));
        checks.Add(await CheckGatewayAsync(ct));
        checks.Add(await CheckSystemDnsAsync(ct));
        checks.Add(await CheckContainerResolverAsync(ct));
        checks.Add(await CheckVpnEndpointDnsAsync(ct));
        checks.Add(await CheckTunnelAsync(ct));
        checks.Add(await CheckExitAddressAsync(ct));
        checks.Add(await CheckDnsmasqAsync(ct));
        checks.Add(await CheckFirewallDriftAsync(ct));

        return new DiagnosticsReport(DateTimeOffset.UtcNow, checks);
    }

    private async Task<DiagnosticCheck> CheckIpForwardingAsync(CancellationToken ct) =>
        await discovery.IpForwardingEnabledAsync(ct)
            ? new DiagnosticCheck("ip-forward", "IP forwarding", CheckStatus.Pass, "Enabled")
            : new DiagnosticCheck("ip-forward", "IP forwarding", CheckStatus.Fail,
                "net.ipv4.ip_forward is 0, so no traffic can be routed between interfaces",
                "Run deploy/install.sh, or: sudo sysctl -w net.ipv4.ip_forward=1");

    private async Task<DiagnosticCheck> CheckInterfaceAsync(string iface, string id, string name, CancellationToken ct)
    {
        if (!await discovery.InterfaceExistsAsync(iface, ct))
            return new DiagnosticCheck(id, name, CheckStatus.Fail, $"{iface} does not exist",
                $"Check the interface name in .env, and that the adapter is plugged in");

        var address = await discovery.GetInterfaceAddressAsync(iface, ct);
        return address is null
            ? new DiagnosticCheck(id, name, CheckStatus.Warn, $"{iface} exists but has no IPv4 address",
                $"Assign an address, or re-run deploy/install.sh")
            : new DiagnosticCheck(id, name, CheckStatus.Pass, $"{iface} is up on {address}");
    }

    private async Task<DiagnosticCheck> CheckGatewayAsync(CancellationToken ct)
    {
        var gateway = await discovery.GetDefaultGatewayAsync(_options.WanInterface, ct);
        if (gateway is null)
            return new DiagnosticCheck("wan-gateway", "Upstream gateway", CheckStatus.Fail,
                "No default route on the WAN interface, so bypass traffic has nowhere to go",
                "Check the upstream router and the WAN cable");

        var ping = await runner.RunAsync(["ping", "-c", "1", "-W", "2", gateway], allowFailure: true, ct: ct);
        return ping.Success
            ? new DiagnosticCheck("wan-gateway", "Upstream gateway", CheckStatus.Pass, $"{gateway} is reachable")
            : new DiagnosticCheck("wan-gateway", "Upstream gateway", CheckStatus.Fail,
                $"{gateway} did not respond to ping", "Check the upstream router");
    }

    /// <summary>Can we resolve anything at all? This is the check that would have caught the outage first.</summary>
    private async Task<DiagnosticCheck> CheckSystemDnsAsync(CancellationToken ct)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync("cloudflare.com", AddressFamily.InterNetwork, ct);
            return addresses.Length > 0
                ? new DiagnosticCheck("dns-resolve", "DNS resolution", CheckStatus.Pass,
                    $"cloudflare.com resolved to {addresses[0]}")
                : new DiagnosticCheck("dns-resolve", "DNS resolution", CheckStatus.Fail,
                    "Lookup returned no addresses", "Check the resolvers in .env (ROUTER_UPSTREAMDNS)");
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return new DiagnosticCheck("dns-resolve", "DNS resolution", CheckStatus.Fail,
                $"Cannot resolve names: {ex.Message}",
                "The configured resolver is not answering. Check /etc/resolv.conf inside this container.");
        }
    }

    /// <summary>
    /// Docker snapshots /etc/resolv.conf into the container when it is created and never
    /// refreshes it. Fixing the host therefore does not fix a running container — that
    /// second-order failure cost a long debugging session once already.
    /// </summary>
    private async Task<DiagnosticCheck> CheckContainerResolverAsync(CancellationToken ct)
    {
        const string id = "container-resolver";
        const string name = "Container resolver";
        try
        {
            if (!File.Exists("/etc/resolv.conf"))
                return new DiagnosticCheck(id, name, CheckStatus.Warn, "No /etc/resolv.conf in this container");

            var content = await File.ReadAllTextAsync("/etc/resolv.conf", ct);
            var servers = content.Split('\n')
                .Where(l => l.TrimStart().StartsWith("nameserver", StringComparison.OrdinalIgnoreCase))
                .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1))
                .Where(s => s is not null)
                .ToList();

            if (servers.Count == 0)
                return new DiagnosticCheck(id, name, CheckStatus.Fail, "No nameserver entries",
                    "Restart this container so Docker regenerates its resolv.conf");

            foreach (var server in servers)
            {
                var probe = await runner.RunAsync(
                    ["ping", "-c", "1", "-W", "2", server!], allowFailure: true, ct: ct);
                if (probe.Success)
                    return new DiagnosticCheck(id, name, CheckStatus.Pass, $"Using {string.Join(", ", servers)}");
            }

            return new DiagnosticCheck(id, name, CheckStatus.Fail,
                $"None of the configured resolvers responded: {string.Join(", ", servers)}",
                "This container is holding a stale resolv.conf. Restart it: docker restart pirouter-api");
        }
        catch (Exception ex)
        {
            logger.LogDebug("Resolver check failed: {Error}", ex.Message);
            return new DiagnosticCheck(id, name, CheckStatus.Warn, $"Could not inspect resolv.conf: {ex.Message}");
        }
    }

    /// <summary>wg-quick cannot bring a tunnel up if the endpoint hostname will not resolve.</summary>
    private async Task<DiagnosticCheck> CheckVpnEndpointDnsAsync(CancellationToken ct)
    {
        const string id = "vpn-endpoint-dns";
        const string name = "VPN endpoint DNS";

        var profile = config.Current.ActiveVpnProfile;
        if (string.IsNullOrWhiteSpace(profile))
            return new DiagnosticCheck(id, name, CheckStatus.Pass, "No VPN profile selected");

        var host = vpn.ReadEndpointHost(profile);
        if (host is null)
            return new DiagnosticCheck(id, name, CheckStatus.Warn, $"No endpoint found in profile '{profile}'");

        if (IPAddress.TryParse(host, out _))
            return new DiagnosticCheck(id, name, CheckStatus.Pass, $"{host} is a literal address, no DNS needed");

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct);
            return new DiagnosticCheck(id, name, CheckStatus.Pass, $"{host} resolves to {addresses[0]}");
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return new DiagnosticCheck(id, name, CheckStatus.Fail,
                $"Cannot resolve the VPN endpoint {host}",
                "The tunnel cannot come up until DNS works. Check the DNS resolution check above.");
        }
    }

    private async Task<DiagnosticCheck> CheckTunnelAsync(CancellationToken ct)
    {
        const string id = "vpn-tunnel";
        const string name = "VPN tunnel";

        var status = await vpn.GetStatusAsync(ct);
        if (string.IsNullOrWhiteSpace(config.Current.ActiveVpnProfile))
            return new DiagnosticCheck(id, name, CheckStatus.Pass, "No VPN profile selected");

        if (!status.Up)
            return new DiagnosticCheck(id, name, CheckStatus.Fail,
                $"{_options.VpnInterface} is not up", "Connect a profile from the VPN page");

        var age = status.PrimaryPeer?.HandshakeAge;
        if (age is null)
            return new DiagnosticCheck(id, name, CheckStatus.Warn,
                "Interface is up but has never completed a handshake",
                "The endpoint may be unreachable or the keys may be wrong");

        var max = TimeSpan.FromSeconds(_options.VpnStaleHandshakeSeconds);
        return age <= max
            ? new DiagnosticCheck(id, name, CheckStatus.Pass,
                $"Handshake {age.Value.TotalSeconds:0}s ago, {Bytes(status.TotalReceived)} in / {Bytes(status.TotalSent)} out")
            : new DiagnosticCheck(id, name, CheckStatus.Fail,
                $"Last handshake was {age.Value.TotalMinutes:0.0} minutes ago",
                "The watchdog will reconnect automatically; check WAN connectivity if this persists");
    }

    /// <summary>Proves traffic is genuinely leaving through the tunnel, not merely that it is up.</summary>
    private async Task<DiagnosticCheck> CheckExitAddressAsync(CancellationToken ct)
    {
        const string id = "exit-ip";
        const string name = "Tunnel exit address";

        var status = await vpn.GetStatusAsync(ct);
        if (!status.Up)
            return new DiagnosticCheck(id, name, CheckStatus.Pass, "Skipped, no tunnel");

        // The comparison has to be against the WAN interface explicitly. An unbound curl
        // follows the host's default route, which wg-quick has already pointed at the
        // tunnel — so both requests would exit the same way and the check would report a
        // problem on a perfectly healthy router.
        var viaWan = await runner.RunAsync(
            ["curl", "-s", "--max-time", "8", "--interface", _options.WanInterface, "https://ifconfig.me"],
            allowFailure: true, ct: ct);
        var viaTunnel = await runner.RunAsync(
            ["curl", "-s", "--max-time", "8", "--interface", _options.VpnInterface, "https://ifconfig.me"],
            allowFailure: true, ct: ct);

        var wanIp = viaWan.Stdout.Trim();
        var tunnelIp = viaTunnel.Stdout.Trim();

        if (string.IsNullOrWhiteSpace(tunnelIp))
            return new DiagnosticCheck(id, name, CheckStatus.Fail,
                "No response when routing through the tunnel",
                "The tunnel is up but is not carrying traffic");

        if (string.IsNullOrWhiteSpace(wanIp))
            return new DiagnosticCheck(id, name, CheckStatus.Pass,
                $"Traffic exits at {tunnelIp} through the tunnel (could not compare against the WAN)");

        return wanIp == tunnelIp
            ? new DiagnosticCheck(id, name, CheckStatus.Fail,
                $"Traffic exits at {tunnelIp} whether it goes via the tunnel or the WAN - the VPN is not taking effect",
                "Check the routing rules on the System page")
            : new DiagnosticCheck(id, name, CheckStatus.Pass,
                $"Exits at {tunnelIp} through the tunnel, {wanIp} without it");
    }

    private async Task<DiagnosticCheck> CheckDnsmasqAsync(CancellationToken ct) =>
        await dnsmasq.IsRunningAsync(ct)
            ? new DiagnosticCheck("dnsmasq", "DHCP / DNS server", CheckStatus.Pass, "dnsmasq is running")
            : new DiagnosticCheck("dnsmasq", "DHCP / DNS server", CheckStatus.Fail,
                "dnsmasq is not running, so LAN clients cannot get an address or resolve names",
                "docker compose up -d pirouter-dnsmasq");

    /// <summary>Compares live firewall state against what the compiler says it should be.</summary>
    private async Task<DiagnosticCheck> CheckFirewallDriftAsync(CancellationToken ct)
    {
        const string id = "firewall-drift";
        const string name = "Firewall state";
        try
        {
            var diff = await reconciler.DiffAsync(ct);
            if (diff.InSync)
                return new DiagnosticCheck(id, name, CheckStatus.Pass, "Live rules match the desired state");

            var parts = new List<string>();
            if (diff.MissingChains.Count > 0) parts.Add($"{diff.MissingChains.Count} missing chain(s)");
            if (diff.Missing.Count > 0) parts.Add($"{diff.Missing.Count} missing rule(s)");
            if (diff.Unexpected.Count > 0) parts.Add($"{diff.Unexpected.Count} unexpected rule(s)");

            return new DiagnosticCheck(id, name, CheckStatus.Warn, string.Join(", ", parts),
                "The reconciler repairs this automatically within a few seconds");
        }
        catch (Exception ex)
        {
            return new DiagnosticCheck(id, name, CheckStatus.Warn, $"Could not compare rules: {ex.Message}");
        }
    }

    private static string Bytes(long value) => value switch
    {
        < 1024 => $"{value} B",
        < 1024 * 1024 => $"{value / 1024.0:0.0} KiB",
        < 1024L * 1024 * 1024 => $"{value / (1024.0 * 1024):0.0} MiB",
        _ => $"{value / (1024.0 * 1024 * 1024):0.00} GiB",
    };
}
