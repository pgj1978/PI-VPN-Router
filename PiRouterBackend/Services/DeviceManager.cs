using PiRouterBackend.Models;
using System.Net;

namespace PiRouterBackend.Services;

public interface IDeviceManager
{
    Task Initialize();
    Task<object> ListDevices();
    Task<object> SetDeviceBypass(string mac, bool bypass);
    Task<object> SetStaticDeviceIp(string mac, string? ip);
    Task<object> GetSystemInfo();
}

public class DeviceManager : IDeviceManager
{
    private readonly IConfigManager _configManager;
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<DeviceManager> _logger;

    private const string LAN_IFACE = "eth1";
    private const string WAN_IFACE = "eth0";
    private const string GATEWAY_IP = "192.168.5.1";
    private const string BYPASS_TABLE = "100";
    private const string PI_LAN_IP = "192.168.10.1";
    private const string STATIC_LEASES_FILE = "/etc/dnsmasq.d/02-static-leases.conf";

    public DeviceManager(IConfigManager configManager, IProcessRunner processRunner, ILogger<DeviceManager> logger)
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
            foreach (var device in config.Devices.Where(d => d.BypassVpn))
            {
                _logger.LogInformation("Restoring bypass for device: {Mac}", device.Mac);
                await ApplyDeviceRouting(device.Mac, true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing device bypasses");
        }
    }

    public async Task<object> ListDevices()
    {
        try
        {
            var config = _configManager.LoadConfig();
            var devices = new List<Dictionary<string, object>>();

            // Read active leases
            var activeLeases = new Dictionary<string, (string Ip, string Hostname)>();
            var leasesFile = "/var/lib/misc/dnsmasq.leases";
            if (File.Exists(leasesFile))
            {
                var lines = await File.ReadAllLinesAsync(leasesFile);
                foreach (var line in lines)
                {
                    var parts = line.Split(' ');
                    if (parts.Length >= 4)
                    {
                        var mac = parts[1].ToLower();
                        var ip = parts[2];
                        var hostname = parts[3] != "*" ? parts[3] : "Unknown";
                        activeLeases[mac] = (ip, hostname);
                    }
                }
            }

            // Read static leases configuration
            var staticLeases = new Dictionary<string, string>(); // Mac -> IP
            if (File.Exists(STATIC_LEASES_FILE))
            {
                var lines = await File.ReadAllLinesAsync(STATIC_LEASES_FILE);
                foreach (var line in lines)
                {
                    if (line.Trim().StartsWith("dhcp-host="))
                    {
                        var parts = line.Substring(10).Split(',');
                        if (parts.Length >= 2)
                        {
                            var mac = parts[0].Trim().ToLower();
                            // Find the IP part
                            foreach (var part in parts.Skip(1))
                            {
                                if (IPAddress.TryParse(part.Trim(), out _))
                                {
                                    staticLeases[mac] = part.Trim();
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            // Merge Active Leases
            foreach (var kvp in activeLeases)
            {
                var mac = kvp.Key;
                var savedDevice = config.Devices.FirstOrDefault(d => 
                            d.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase));
                
                devices.Add(new Dictionary<string, object>
                {
                    { "mac", mac },
                    { "ip", kvp.Value.Ip },
                    { "hostname", kvp.Value.Hostname },
                    { "bypass_vpn", savedDevice?.BypassVpn ?? false },
                    { "static_ip", staticLeases.ContainsKey(mac) ? staticLeases[mac] : null }
                });
            }

            // Add Static configurations that aren't currently active
            foreach (var kvp in staticLeases)
            {
                if (!devices.Any(d => d["mac"].ToString() == kvp.Key))
                {
                    var mac = kvp.Key;
                    var savedDevice = config.Devices.FirstOrDefault(d => 
                            d.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase));

                    devices.Add(new Dictionary<string, object>
                    {
                        { "mac", mac },
                        { "ip", null }, // Not active
                        { "hostname", "Static Device" }, // We might not know the name
                        { "bypass_vpn", savedDevice?.BypassVpn ?? false },
                        { "static_ip", kvp.Value }
                    });
                }
            }

            return new { devices };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing devices");
            return new { devices = new List<object>(), error = ex.Message };
        }
    }

    public async Task<object> SetDeviceBypass(string mac, bool bypass)
    {
        try
        {
            mac = System.Net.WebUtility.UrlDecode(mac).ToLower();

            var config = _configManager.LoadConfig();
            var device = config.Devices.FirstOrDefault(d => 
                d.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase));

            if (device != null)
            {
                device.BypassVpn = bypass;
            }
            else
            {
                string? ip = await GetIpForMac(mac);
                config.Devices.Add(new Device
                {
                    Mac = mac,
                    Ip = ip ?? "0.0.0.0",
                    Hostname = null,
                    BypassVpn = bypass
                });
            }

            await _configManager.SaveConfigAsync(config);
            await ApplyDeviceRouting(mac, bypass);

            return new { status = "updated", mac, bypass_vpn = bypass };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting device bypass");
            return new { success = false, error = ex.Message };
        }
    }

    public async Task<object> SetStaticDeviceIp(string mac, string? ip)
    {
        try
        {
            mac = System.Net.WebUtility.UrlDecode(mac).ToLower();
            _logger.LogInformation("Setting static IP for {Mac}: {Ip}", mac, ip ?? "REMOVE");
            
            // Step 1: Update static leases config file
            var lines = new List<string>();
            if (File.Exists(STATIC_LEASES_FILE))
            {
                lines = (await File.ReadAllLinesAsync(STATIC_LEASES_FILE)).ToList();
            }

            // Remove existing entry for this MAC
            lines.RemoveAll(l => l.Trim().StartsWith($"dhcp-host={mac}", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(ip))
            {
                // Validate IP format
                if (!IPAddress.TryParse(ip, out _))
                {
                    return new { success = false, error = $"Invalid IP address: {ip}" };
                }
                // Add new entry
                lines.Add($"dhcp-host={mac},{ip}");
                _logger.LogInformation("Added static lease: dhcp-host={Mac},{Ip}", mac, ip);
            }
            else
            {
                _logger.LogInformation("Removed static lease for {Mac}", mac);
            }

            await File.WriteAllLinesAsync(STATIC_LEASES_FILE, lines);

            // Step 2: Stop dnsmasq service
            _logger.LogInformation("Stopping dnsmasq service");
            // Try using init system - works with host network mode
            var (stopSuccess, stopOutput) = await _processRunner.RunCommandAsync(new[] { "/etc/init.d/dnsmasq", "stop" });
            
            if (!stopSuccess)
            {
                _logger.LogWarning("Could not stop dnsmasq with init.d, trying service command");
                stopSuccess = (await _processRunner.RunCommandAsync(new[] { "service", "dnsmasq", "stop" })).Item1;
            }
            
            _logger.LogInformation("Waiting for dnsmasq to fully stop");
            await Task.Delay(2000);

            // Step 3: Clear existing lease while dnsmasq is stopped
            try
            {
                var leaseFile = "/var/lib/misc/dnsmasq.leases";
                if (File.Exists(leaseFile))
                {
                    // Use sed for atomic in-place removal of the lease for the specific MAC address
                    var (sedSuccess, sedOutput) = await _processRunner.RunCommandAsync(new[] { 
                        "sed", "-i", $@"/{mac}/d", leaseFile 
                    });

                    if (sedSuccess)
                    {
                        _logger.LogInformation("Cleared lease(s) for {Mac} using sed.", mac);
                    }
                    else
                    {
                        _logger.LogWarning("Failed to clear lease for {Mac} using sed. Output: {Output}", mac, sedOutput);
                    }
                }
                else
                {
                    _logger.LogInformation("Lease file {LeaseFile} not found, no leases to clear for {Mac}.", leaseFile, mac);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clear existing lease for {Mac}", mac);
            }

            // Step 4: Clear dnsmasq cache
            try
            {
                _logger.LogInformation("Clearing dnsmasq cache directory");
                var dnsmasqDbDir = "/var/lib/dnsmasq";
                if (System.IO.Directory.Exists(dnsmasqDbDir))
                {
                    var files = System.IO.Directory.GetFiles(dnsmasqDbDir);
                    foreach (var file in files)
                    {
                        try
                        {
                            System.IO.File.Delete(file);
                            _logger.LogInformation("Deleted cache file: {File}", file);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clear dnsmasq cache");
            }

            // Step 5: Start dnsmasq service
            _logger.LogInformation("Starting dnsmasq service");
            var (startSuccess, startOutput) = await _processRunner.RunCommandAsync(new[] { "/etc/init.d/dnsmasq", "start" });
            
            if (!startSuccess)
            {
                _logger.LogWarning("Could not start dnsmasq with init.d, trying service command");
                startSuccess = (await _processRunner.RunCommandAsync(new[] { "service", "dnsmasq", "start" })).Item1;
            }

            if (!startSuccess)
            {
                _logger.LogError("Failed to start dnsmasq");
                return new { success = false, error = "Failed to restart dnsmasq service" };
            }

            _logger.LogInformation("Waiting for dnsmasq to fully start");
            await Task.Delay(1500);

            // Step 6: Send reload signal
            try
            {
                await _processRunner.RunCommandAsync(new[] { "killall", "-USR1", "dnsmasq" });
            }
            catch
            {
                // Ignore failures - not critical
            }

            _logger.LogInformation("Static IP assignment complete for {Mac}: {Ip}", mac, ip ?? "REMOVED");

            return new { success = true, mac, static_ip = ip };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting static IP for {Mac}", mac);
            return new { success = false, error = ex.Message };
        }
    }

    public async Task<object> GetSystemInfo()
    {
        try
        {
            var (ifSuccess, ifOutput) = await _processRunner.RunCommandAsync(new[] { "ip", "addr", "show" });
            var (routeSuccess, routeOutput) = await _processRunner.RunCommandAsync(new[] { "ip", "route", "show" });

            return new
            {
                interfaces = ifSuccess ? ifOutput : "N/A",
                routes = routeSuccess ? routeOutput : "N/A"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting system info");
            return new { error = ex.Message };
        }
    }

    private async Task<string?> GetIpForMac(string mac)
    {
        try
        {
            var leasesFile = "/var/lib/misc/dnsmasq.leases";
            if (File.Exists(leasesFile))
            {
                var lines = await File.ReadAllLinesAsync(leasesFile);
                foreach (var line in lines)
                {
                    var parts = line.Split(' ');
                    if (parts.Length >= 4 && parts[1].Equals(mac, StringComparison.OrdinalIgnoreCase))
                    {
                        return parts[2];
                    }
                }
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Error reading leases"); }

        try
        {
            if (File.Exists(STATIC_LEASES_FILE))
            {
                var lines = await File.ReadAllLinesAsync(STATIC_LEASES_FILE);
                foreach (var line in lines)
                {
                    if (line.Trim().StartsWith($"dhcp-host={mac}", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Substring(10).Split(',');
                        foreach(var part in parts)
                        {
                             if (IPAddress.TryParse(part.Trim(), out _)) return part.Trim();
                        }
                    }
                }
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Error reading static config"); }

        return null;
    }

    private async Task EnsureBypassInfrastructure()
    {
        await _processRunner.RunCommandAsync(new[] { 
            "ip", "route", "replace", "default", 
            "via", GATEWAY_IP, "dev", WAN_IFACE, "table", BYPASS_TABLE 
        });

        var (success, output) = await _processRunner.RunCommandAsync(new[] { "ip", "rule", "show" });
        if (!output.Contains($"fwmark 0x{int.Parse(BYPASS_TABLE):x} lookup {BYPASS_TABLE}"))
        {
             await _processRunner.RunCommandAsync(new[] { 
                "ip", "rule", "add", "fwmark", BYPASS_TABLE, 
                "lookup", BYPASS_TABLE, "priority", "1" 
            });
        }

        await _processRunner.RunCommandAsync(new[] {
            "iptables", "-t", "nat", "-D", "POSTROUTING", "-o", WAN_IFACE,
            "-m", "mark", "--mark", BYPASS_TABLE, "-j", "MASQUERADE"
        }, logFailure: false);
        await _processRunner.RunCommandAsync(new[] {
            "iptables", "-t", "nat", "-A", "POSTROUTING", "-o", WAN_IFACE,
            "-m", "mark", "--mark", BYPASS_TABLE, "-j", "MASQUERADE"
        });
    }

    private async Task ApplyDeviceRouting(string mac, bool bypass)
    {
        try
        {
            var ip = await GetIpForMac(mac);
            if (string.IsNullOrEmpty(ip))
            {
                _logger.LogWarning("Could not find IP for MAC {Mac}, skipping routing", mac);
                return;
            }

            if (bypass)
            {
                await EnsureBypassInfrastructure();
                _logger.LogInformation("Applying bypass for device {Mac} ({Ip})", mac, ip);

                await _processRunner.RunCommandAsync(new[] {
                    "iptables", "-t", "mangle", "-D", "PREROUTING", 
                    "-i", LAN_IFACE, "-s", ip, "!", "-d", PI_LAN_IP,
                    "-j", "MARK", "--set-mark", BYPASS_TABLE
                }, logFailure: false);
                await _processRunner.RunCommandAsync(new[] {
                    "iptables", "-t", "mangle", "-A", "PREROUTING", 
                    "-i", LAN_IFACE, "-s", ip, "!", "-d", PI_LAN_IP,
                    "-j", "MARK", "--set-mark", BYPASS_TABLE
                });

                await _processRunner.RunCommandAsync(new[] {
                    "iptables", "-D", "FORWARD", "-i", LAN_IFACE, "-o", WAN_IFACE, "-s", ip, "-j", "ACCEPT"
                }, logFailure: false);
                await _processRunner.RunCommandAsync(new[] {
                    "iptables", "-A", "FORWARD", "-i", LAN_IFACE, "-o", WAN_IFACE, "-s", ip, "-j", "ACCEPT"
                });

                await _processRunner.RunCommandAsync(new[] {
                    "iptables", "-D", "FORWARD", "-i", "br+", "-o", LAN_IFACE, "-d", ip, 
                    "-m", "state", "--state", "ESTABLISHED,RELATED", "-j", "ACCEPT"
                }, logFailure: false);
                await _processRunner.RunCommandAsync(new[] {
                    "iptables", "-A", "FORWARD", "-i", "br+", "-o", LAN_IFACE, "-d", ip, 
                    "-m", "state", "--state", "ESTABLISHED,RELATED", "-j", "ACCEPT"
                });

                await _processRunner.RunCommandAsync(new[] {
                    "iptables", "-D", "FORWARD", "-i", WAN_IFACE, "-o", LAN_IFACE, "-d", ip, 
                    "-m", "state", "--state", "ESTABLISHED,RELATED", "-j", "ACCEPT"
                }, logFailure: false);
                await _processRunner.RunCommandAsync(new[] {
                    "iptables", "-A", "FORWARD", "-i", WAN_IFACE, "-o", LAN_IFACE, "-d", ip, 
                    "-m", "state", "--state", "ESTABLISHED,RELATED", "-j", "ACCEPT"
                });
            }
            else
            {
                _logger.LogInformation("Removing bypass for device {Mac} ({Ip})", mac, ip);

                await _processRunner.RunCommandAsync(new[] {
                    "iptables", "-t", "mangle", "-D", "PREROUTING", 
                    "-i", LAN_IFACE, "-s", ip, "!", "-d", PI_LAN_IP,
                    "-j", "MARK", "--set-mark", BYPASS_TABLE
                }, logFailure: false);

                await _processRunner.RunCommandAsync(new[] {
                    "iptables", "-D", "FORWARD", "-i", LAN_IFACE, "-o", WAN_IFACE, "-s", ip, "-j", "ACCEPT"
                }, logFailure: false);

                await _processRunner.RunCommandAsync(new[] {
                    "iptables", "-D", "FORWARD", "-i", "br+", "-o", LAN_IFACE, "-d", ip, 
                    "-m", "state", "--state", "ESTABLISHED,RELATED", "-j", "ACCEPT"
                }, logFailure: false);

                await _processRunner.RunCommandAsync(new[] {
                    "iptables", "-D", "FORWARD", "-i", WAN_IFACE, "-o", LAN_IFACE, "-d", ip, 
                    "-m", "state", "--state", "ESTABLISHED,RELATED", "-j", "ACCEPT"
                }, logFailure: false);
            }

            await _processRunner.RunCommandAsync(new[] { "ip", "route", "flush", "cache" });
            await _processRunner.RunCommandAsync(new[] { "conntrack", "-D", "-s", ip }, logFailure: false);
            await _processRunner.RunCommandAsync(new[] { "conntrack", "-D", "-d", ip }, logFailure: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying device routing");
        }
    }
}
