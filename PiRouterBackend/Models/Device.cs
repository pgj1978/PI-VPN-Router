namespace PiRouterBackend.Models;

public class Device
{
    public required string Mac { get; set; }
    public required string Ip { get; set; }
    public string? Hostname { get; set; }
    public bool BypassVpn { get; set; } = false;
}
