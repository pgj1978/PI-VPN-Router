namespace PiRouterBackend.Models;

public class RouterConfig
{
    public string? ActiveVpn { get; set; }
    public bool KillSwitchEnabled { get; set; } = false;
    public List<Device> Devices { get; set; } = new();
    public List<DomainBypass> DomainBypasses { get; set; } = new();
}
