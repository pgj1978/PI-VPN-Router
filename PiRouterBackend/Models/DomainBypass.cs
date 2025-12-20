namespace PiRouterBackend.Models;

public class DomainBypass
{
    public required string Domain { get; set; }
    public bool Enabled { get; set; } = true;
}
