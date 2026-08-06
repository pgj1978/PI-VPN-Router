namespace PiRouter.Core.Firewall;

/// <summary>
/// The complete input to rule compilation: persisted intent already merged with
/// discovered runtime facts (current leases, tunnel state, resolved domains).
///
/// This is deliberately a plain immutable record with no I/O so that
/// <see cref="RuleCompiler"/> is a pure function and can be tested off-device.
/// </summary>
public sealed record RouterState
{
    public required string LanInterface { get; init; }
    public required string WanInterface { get; init; }
    public required string VpnInterface { get; init; }

    /// <summary>Pi's own LAN address, e.g. "192.168.20.1".</summary>
    public required string LanIp { get; init; }

    /// <summary>LAN network, e.g. "192.168.20.0/24".</summary>
    public required string LanNetwork { get; init; }

    /// <summary>Upstream gateway, discovered from the WAN default route. Null if the WAN is down.</summary>
    public string? WanGateway { get; init; }

    /// <summary>True when the WireGuard interface exists and is configured.</summary>
    public required bool VpnUp { get; init; }

    /// <summary>Routing table wg-quick allocated for the tunnel, discovered from its fwmark.</summary>
    public int? VpnTableId { get; init; }

    public required bool KillSwitchEnabled { get; init; }

    public int BypassMark { get; init; } = 100;
    public int BypassRulePriority { get; init; } = 1;
    public int VpnRulePriority { get; init; } = 20000;
    public int VpnMss { get; init; } = 1360;

    /// <summary>Current IPs of devices the user marked as bypassing, resolved fresh from DHCP leases.</summary>
    public IReadOnlyList<string> BypassDeviceIps { get; init; } = [];

    /// <summary>All A records for every enabled bypass domain.</summary>
    public IReadOnlyList<string> BypassDomainIps { get; init; } = [];

    /// <summary>Prefixes that must fall back to the main routing table instead of entering the tunnel.</summary>
    public IReadOnlyList<string> TunnelExcludedPrefixes { get; init; } = [];

    /// <summary>
    /// True when traffic that is not explicitly bypassed must not be allowed out of the WAN.
    /// Note this is independent of tunnel state: with the kill switch on and the tunnel up,
    /// anything still trying to leave via the WAN is a leak.
    /// </summary>
    public bool BlockNonBypassedWanEgress => KillSwitchEnabled;
}
