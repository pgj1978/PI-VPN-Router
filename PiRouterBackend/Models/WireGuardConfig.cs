namespace PiRouterBackend.Models;

public class WireGuardConfig
{
    public required string Name { get; set; }
    public required string Filename { get; set; }
    public bool Active { get; set; } = false;
}
