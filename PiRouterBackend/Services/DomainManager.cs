using PiRouterBackend.Models;
using System.Net;

namespace PiRouterBackend.Services;

public interface IDomainManager
{
    Task Initialize();
    Task<object> ListDomainBypasses();
    Task<object> AddDomainBypass(string domain);
    Task<object> RemoveDomainBypass(string domain);
}

public class DomainManager : IDomainManager
{
    private readonly IConfigManager _configManager;
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<DomainManager> _logger;

    private const string LAN_IFACE = "eth1";
    private const string WAN_IFACE = "eth0";
    private const string GATEWAY_IP = "192.168.5.1";
    private const string BYPASS_TABLE = "100";

    public DomainManager(IConfigManager configManager, IProcessRunner processRunner, ILogger<DomainManager> logger)
    {
        _configManager = configManager;
        _processRunner = processRunner;
        _logger = logger;
    }

    public async Task Initialize()
    {
        try
        {
            var config = _configManager.LoadConfig();
            foreach (var bypass in config.DomainBypasses.Where(d => d.Enabled))
            {
                _logger.LogInformation("Restoring bypass for domain: {Domain}", bypass.Domain);
                await ApplyDomainBypass(bypass.Domain, true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing domain bypasses");
        }
    }

    public async Task<object> ListDomainBypasses()
    {
        try
        {
            var config = _configManager.LoadConfig();
            return await Task.FromResult(new { domains = config.DomainBypasses });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing domain bypasses");
            return new { domains = new List<DomainBypass>(), error = ex.Message };
        }
    }

    public async Task<object> AddDomainBypass(string domain)
    {
        try
        {
            var config = _configManager.LoadConfig();
            
            if (config.DomainBypasses.Any(d => d.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase)))
            {
                return new { success = false, error = "Domain already exists in bypass list" };
            }

            // Apply routing rules
            await ApplyDomainBypass(domain, true);

            config.DomainBypasses.Add(new DomainBypass { Domain = domain, Enabled = true });
            await _configManager.SaveConfigAsync(config);

            _logger.LogInformation("Added domain bypass: {Domain}", domain);
            return new { success = true, domain };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding domain bypass");
            return new { success = false, error = ex.Message };
        }
    }

    public async Task<object> RemoveDomainBypass(string domain)
    {
        try
        {
            var config = _configManager.LoadConfig();
            var domainBypass = config.DomainBypasses.FirstOrDefault(d => 
                d.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase));

            if (domainBypass == null)
            {
                return new { success = false, error = "Domain not found in bypass list" };
            }

            // Remove routing rules
            await ApplyDomainBypass(domain, false);

            config.DomainBypasses.Remove(domainBypass);
            await _configManager.SaveConfigAsync(config);

            _logger.LogInformation("Removed domain bypass: {Domain}", domain);
            return new { success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing domain bypass");
            return new { success = false, error = ex.Message };
        }
    }

    private async Task EnsureBypassInfrastructure()
    {
        // 1. Ensure Table 200 has a default route to the ISP
        await _processRunner.RunCommandAsync(new[] { 
            "ip", "route", "replace", "default", 
            "via", GATEWAY_IP, "dev", WAN_IFACE, "table", BYPASS_TABLE 
        });

        // 2. Ensure the Kernel looks for the FWMark 100
        var (success, output) = await _processRunner.RunCommandAsync(new[] { "ip", "rule", "show" });
        // 100 decimal is 0x64 hex. ip rule show usually outputs in hex.
        if (!output.Contains($"fwmark 0x{int.Parse(BYPASS_TABLE):x} lookup {BYPASS_TABLE}"))
        {
             await _processRunner.RunCommandAsync(new[] { 
                "ip", "rule", "add", "fwmark", BYPASS_TABLE, 
                "lookup", BYPASS_TABLE, "priority", "1" 
            });
        }

        // 3. Ensure NAT is enabled for marked packets leaving eth0
        // Delete first to avoid duplicates
        await _processRunner.RunCommandAsync(new[] {
            "iptables", "-t", "nat", "-D", "POSTROUTING", "-o", WAN_IFACE,
            "-m", "mark", "--mark", BYPASS_TABLE, "-j", "MASQUERADE"
        });
        await _processRunner.RunCommandAsync(new[] {
            "iptables", "-t", "nat", "-A", "POSTROUTING", "-o", WAN_IFACE,
            "-m", "mark", "--mark", BYPASS_TABLE, "-j", "MASQUERADE"
        });
    }

    private async Task ApplyDomainBypass(string domain, bool enable)
    {
        try 
        {
            var ips = await ResolveDomain(domain);
            
            if (enable && ips.Count > 0)
            {
                await EnsureBypassInfrastructure();
            }

            foreach (var ip in ips)
            {
                if (enable)
                {
                    _logger.LogInformation("Adding bypass rules for domain {Domain} ({Ip})", domain, ip);
                    
                    // 1. Mangle Rule (Marking)
                    // Delete old rule first
                    await _processRunner.RunCommandAsync(new[] {
                        "iptables", "-t", "mangle", "-D", "PREROUTING", 
                        "-i", LAN_IFACE, "-d", ip, 
                        "-j", "MARK", "--set-mark", BYPASS_TABLE
                    }, logFailure: false);

                    // Add new rule
                    await _processRunner.RunCommandAsync(new[] {
                        "iptables", "-t", "mangle", "-A", "PREROUTING", 
                        "-i", LAN_IFACE, "-d", ip, 
                        "-j", "MARK", "--set-mark", BYPASS_TABLE
                    });

                    // 2. Forward Rule (Outbound)
                    // Delete old rule first
                    await _processRunner.RunCommandAsync(new[] {
                        "iptables", "-D", "FORWARD", "-i", LAN_IFACE, "-o", WAN_IFACE, "-d", ip, "-j", "ACCEPT"
                    }, logFailure: false);

                    // Add new rule
                    await _processRunner.RunCommandAsync(new[] {
                        "iptables", "-A", "FORWARD", "-i", LAN_IFACE, "-o", WAN_IFACE, "-d", ip, "-j", "ACCEPT"
                    });

                    // 3. Forward Rule (Inbound/Return)
                    // Delete old rule first
                    await _processRunner.RunCommandAsync(new[] {
                        "iptables", "-D", "FORWARD", "-i", WAN_IFACE, "-o", LAN_IFACE, "-s", ip, 
                        "-m", "state", "--state", "ESTABLISHED,RELATED", "-j", "ACCEPT"
                    }, logFailure: false);

                    // Add new rule
                    await _processRunner.RunCommandAsync(new[] {
                        "iptables", "-A", "FORWARD", "-i", WAN_IFACE, "-o", LAN_IFACE, "-s", ip, 
                        "-m", "state", "--state", "ESTABLISHED,RELATED", "-j", "ACCEPT"
                    });
                }
                else
                {
                    _logger.LogInformation("Removing bypass rules for {Domain} ({Ip})", domain, ip);
                    
                    // Remove Mangle Rule
                    await _processRunner.RunCommandAsync(new[] {
                        "iptables", "-t", "mangle", "-D", "PREROUTING", 
                        "-i", LAN_IFACE, "-d", ip, 
                        "-j", "MARK", "--set-mark", BYPASS_TABLE
                    }, logFailure: false);

                    // Remove Forward Rule (Outbound)
                    await _processRunner.RunCommandAsync(new[] {
                        "iptables", "-D", "FORWARD", "-i", LAN_IFACE, "-o", WAN_IFACE, "-d", ip, "-j", "ACCEPT"
                    }, logFailure: false);

                    // Remove Forward Rule (Inbound)
                    await _processRunner.RunCommandAsync(new[] {
                        "iptables", "-D", "FORWARD", "-i", WAN_IFACE, "-o", LAN_IFACE, "-s", ip, 
                        "-m", "state", "--state", "ESTABLISHED,RELATED", "-j", "ACCEPT"
                    }, logFailure: false);
                }
            }
            await _processRunner.RunCommandAsync(new[] { "ip", "route", "flush", "cache" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying domain bypass for {Domain}", domain);
        }
    }

    private async Task<List<string>> ResolveDomain(string domain)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(domain);
            return addresses
                .Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(ip => ip.ToString())
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve domain {Domain}", domain);
            return new List<string>();
        }
    }
}