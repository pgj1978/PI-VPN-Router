namespace PiRouter.Core.Models;

/// <summary>Persisted user intent. Never contains discovered runtime state.</summary>
public sealed class RouterConfig
{
    public string? ActiveVpnProfile { get; set; }
    public bool KillSwitchEnabled { get; set; }
    public List<DeviceConfig> Devices { get; set; } = [];
    public List<DomainBypassConfig> DomainBypasses { get; set; } = [];
}

/// <summary>
/// A device the user has expressed intent about. Keyed on MAC only — the previous
/// implementation stored an IP alongside it and matched on that, so a DHCP lease change
/// left rules pointing at an address nobody owned any more.
/// </summary>
public sealed class DeviceConfig
{
    public required string Mac { get; set; }

    /// <summary>Friendly name the user assigned. Falls back to the DHCP hostname in the UI.</summary>
    public string? Label { get; set; }

    public bool BypassVpn { get; set; }

    /// <summary>Pinned DHCP reservation, if the user asked for one.</summary>
    public string? StaticIp { get; set; }
}

public sealed class DomainBypassConfig
{
    public required string Domain { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Last successful resolution. Cached so a transient DNS failure does not tear down working rules.</summary>
    public List<string> LastResolvedIps { get; set; } = [];

    public DateTimeOffset? LastResolvedAt { get; set; }
}
