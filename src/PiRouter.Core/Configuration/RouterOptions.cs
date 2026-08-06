namespace PiRouter.Core.Configuration;

/// <summary>
/// Every piece of network topology the router needs, in exactly one place.
/// Bound from environment / .env at startup. Nothing in this codebase may hardcode
/// an interface name, subnet or gateway anywhere else.
/// </summary>
public sealed class RouterOptions
{
    public const string SectionName = "Router";

    /// <summary>Interface facing the client devices (DHCP is served here).</summary>
    public string LanInterface { get; set; } = "eth1";

    /// <summary>Interface facing the upstream internet router.</summary>
    public string WanInterface { get; set; } = "eth0";

    /// <summary>WireGuard interface brought up by wg-quick.</summary>
    public string VpnInterface { get; set; } = "wg0";

    /// <summary>LAN address of the Pi itself, with prefix, e.g. "192.168.20.1/24".</summary>
    public string LanAddress { get; set; } = "192.168.20.1/24";

    /// <summary>
    /// Upstream gateway. Left null it is auto-detected from the WAN default route,
    /// which is what we want — the previous code hardcoded 192.168.5.1.
    /// </summary>
    public string? WanGateway { get; set; }

    /// <summary>DHCP pool start, e.g. "192.168.20.10".</summary>
    public string DhcpRangeStart { get; set; } = "192.168.20.10";

    /// <summary>DHCP pool end, e.g. "192.168.20.200".</summary>
    public string DhcpRangeEnd { get; set; } = "192.168.20.200";

    public string DhcpLeaseTime { get; set; } = "12h";

    /// <summary>Upstream resolvers handed to LAN clients and used by dnsmasq.</summary>
    public string[] UpstreamDns { get; set; } = ["1.1.1.1", "8.8.8.8"];

    /// <summary>fwmark and routing table number used for bypass traffic.</summary>
    public int BypassMark { get; set; } = 100;

    /// <summary>
    /// Priority of the bypass ip-rule. Must be numerically lower (= higher precedence)
    /// than <see cref="VpnRulePriority"/> or bypass traffic still falls into the tunnel.
    /// </summary>
    public int BypassRulePriority { get; set; } = 1;

    /// <summary>
    /// Priority we relocate wg-quick's "not fwmark" rule to. wg-quick installs it at 0,
    /// which outranks the bypass rule and is why bypass silently did nothing.
    /// </summary>
    public int VpnRulePriority { get; set; } = 20000;

    /// <summary>
    /// MSS for TCP SYNs entering the tunnel. WireGuard MTU 1420 - 60 = 1360.
    /// Only applied to traffic egressing the VPN interface, never to bypass traffic.
    /// </summary>
    public int VpnMss { get; set; } = 1360;

    /// <summary>Prefixes that must never be routed into the tunnel.</summary>
    public string[] TunnelExcludedPrefixes { get; set; } = ["192.168.0.0/16", "172.16.0.0/12", "10.0.0.0/8"];

    /// <summary>How often the reconciler recomputes and repairs firewall state.</summary>
    public int ReconcileIntervalSeconds { get; set; } = 15;

    /// <summary>A handshake older than this means the tunnel is dead and needs reconnecting.</summary>
    public int VpnStaleHandshakeSeconds { get; set; } = 180;

    /// <summary>Set false to compute rules and log them without touching the system.</summary>
    public bool ApplyRules { get; set; } = true;

    /// <summary>
    /// Port the API listens on. Configurable because Windows reserves assorted ranges for
    /// Hyper-V (51430-51529 covers the default here), which makes local dev runs fail to bind.
    /// </summary>
    public int ApiPort { get; set; } = 51508;

    /// <summary>
    /// Bind only to loopback. Useful on a dev machine where the configured LAN address does
    /// not exist on any local interface.
    /// </summary>
    public bool LoopbackOnly { get; set; }

    public string VpnProfilesDirectory { get; set; } = "/app/config/vpn_profiles";
    public string ConfigFilePath { get; set; } = "/app/config/router_config.json";
    public string WireGuardDirectory { get; set; } = "/etc/wireguard";
    public string DnsmasqLeaseFile { get; set; } = "/var/lib/misc/dnsmasq.leases";
    public string DnsmasqConfigDirectory { get; set; } = "/etc/dnsmasq.d";

    /// <summary>Network portion of <see cref="LanAddress"/>, e.g. "192.168.20.0/24".</summary>
    public string LanNetwork => Net.Cidr.NetworkOf(LanAddress);

    /// <summary>Host portion of <see cref="LanAddress"/>, e.g. "192.168.20.1".</summary>
    public string LanIp => Net.Cidr.AddressOf(LanAddress);
}
