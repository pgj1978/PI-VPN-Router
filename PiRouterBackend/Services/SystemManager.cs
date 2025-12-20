using PiRouterBackend.Models;
using System.Net;
using System.Text.RegularExpressions;

namespace PiRouterBackend.Services;

public interface ISystemManager
{
    Task<object> GetSystemInfo();
    Task<object> GetDhcpStatus();
    Task<object> SetDhcpStatus(bool enable, string? startIp, string? endIp, string? leaseTime);
    Task<object> GetEth1IpConfig();
    Task<object> SetEth1IpConfig(string ipAddress, string subnetMask);
}

public class SystemManager : ISystemManager
{
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<SystemManager> _logger;

    private const string DHCP_CONFIG_FILE = "/etc/dnsmasq.d/pirouter-dhcp.conf";
    private const string ETH1_NETWORK_CONFIG_FILE = "/etc/dhcpcd.conf"; // Common on Raspberry Pi OS

    public SystemManager(IProcessRunner processRunner, ILogger<SystemManager> logger)
    {
        _processRunner = processRunner;
        _logger = logger;
    }

    public async Task<object> GetSystemInfo()
    {
        // Re-route to DeviceManager's GetSystemInfo if needed, or implement here
        var (ifSuccess, ifOutput) = await _processRunner.RunCommandAsync(new[] { "ip", "addr", "show" });
        var (routeSuccess, routeOutput) = await _processRunner.RunCommandAsync(new[] { "ip", "route", "show" });

        return new
        {
            interfaces = ifSuccess ? ifOutput : "N/A",
            routes = routeSuccess ? routeOutput : "N/A"
        };
    }

    public async Task<object> GetDhcpStatus()
    {
        try
        {
            if (!File.Exists(DHCP_CONFIG_FILE))
            {
                return new { enabled = false, dhcpRange = (string?)null, leaseTime = (string?)null };
            }

            var content = await File.ReadAllTextAsync(DHCP_CONFIG_FILE);
            var dhcpRangeMatch = Regex.Match(content, @"dhcp-range=(?<startIp>\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}),(?<endIp>\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})");
            var leaseTimeMatch = Regex.Match(content, @"dhcp-lease-time=(?<leaseTime>.*)");

            return new
            {
                enabled = dhcpRangeMatch.Success,
                dhcpRange = dhcpRangeMatch.Success ? $"{dhcpRangeMatch.Groups["startIp"].Value},{dhcpRangeMatch.Groups["endIp"].Value}" : null,
                leaseTime = leaseTimeMatch.Success ? leaseTimeMatch.Groups["leaseTime"].Value : null
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting DHCP status");
            return new { enabled = false, error = ex.Message };
        }
    }

    public async Task<object> SetDhcpStatus(bool enable, string? startIp, string? endIp, string? leaseTime)
    {
        try
        {
            if (enable)
            {
                if (string.IsNullOrEmpty(startIp) || string.IsNullOrEmpty(endIp) || string.IsNullOrEmpty(leaseTime))
                {
                    return new { success = false, error = "DHCP range and lease time are required to enable DHCP" };
                }

                var configContent = $"interface=eth1\ndhcp-range={startIp},{endIp},{leaseTime}\n";
                await File.WriteAllTextAsync(DHCP_CONFIG_FILE, configContent);
            }
            else
            {
                if (File.Exists(DHCP_CONFIG_FILE))
                {
                    File.Delete(DHCP_CONFIG_FILE);
                }
            }

            // Reload dnsmasq
            var (success, output) = await _processRunner.RunCommandAsync(new[] { "systemctl", "restart", "dnsmasq" });
            if (!success)
            {
                _logger.LogError("Failed to reload dnsmasq: {Output}", output);
                return new { success = false, error = $"Failed to reload dnsmasq: {output}" };
            }

            return new { success = true, enabled = enable };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting DHCP status");
            return new { success = false, error = ex.Message };
        }
    }

    public async Task<object> GetEth1IpConfig()
    {
        try
        {
            var (success, output) = await _processRunner.RunCommandAsync(new[] { "ip", "addr", "show", "eth1" });
            if (!success)
            {
                return new { ipAddress = (string?)null, subnetMask = (string?)null, error = output };
            }

            var ipAddressMatch = Regex.Match(output, @"inet (?<ipAddress>\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})/(?<cidr>\d{1,2})");
            string? ipAddress = null;
            string? subnetMask = null;

            if (ipAddressMatch.Success)
            {
                ipAddress = ipAddressMatch.Groups["ipAddress"].Value;
                int cidr = int.Parse(ipAddressMatch.Groups["cidr"].Value);
                subnetMask = CidrToSubnetMask(cidr);
            }

            return new { ipAddress, subnetMask };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting eth1 IP config");
            return new { ipAddress = (string?)null, subnetMask = (string?)null, error = ex.Message };
        }
    }

    public async Task<object> SetEth1IpConfig(string ipAddress, string subnetMask)
    {
        try
        {
            int cidr = SubnetMaskToCidr(subnetMask);
            if (cidr <= 0) // Fix for invalid or 0.0.0.0 mask
            {
                _logger.LogWarning("Invalid CIDR {Cidr} derived from mask {Mask}. Defaulting to 24.", cidr, subnetMask);
                cidr = 24;
            }

            // Flush old address
            await _processRunner.RunCommandAsync(new[] { "ip", "addr", "flush", "dev", "eth1" });
            
            // Add new address
            var (success, output) = await _processRunner.RunCommandAsync(new[] { "ip", "addr", "add", $"{ipAddress}/{cidr}", "dev", "eth1" });
            if (!success)
            {
                _logger.LogError("Failed to set IP address for eth1: {Output}", output);
                return new { success = false, error = $"Failed to set IP address: {output}" };
            }

            // Safety cleanup: Ensure no other IPs remain (in case flush missed something or something re-added it)
            try 
            {
                var (s, o) = await _processRunner.RunCommandAsync(new[] { "ip", "-o", "addr", "show", "eth1" });
                if (s)
                {
                    var lines = o.Split('\n');
                    foreach (var line in lines)
                    {
                        var match = Regex.Match(line, @"inet\s+(\S+)");
                        if (match.Success)
                        {
                            var existingIp = match.Groups[1].Value;
                            // Check if exact match to new IP (ignoring broadcast/scope parts usually not in this regex capture but just in case)
                            // ip -o addr show output example: "inet 192.168.9.1/24 brd ..."
                            // Regex captures "192.168.9.1/24"
                            
                            if (existingIp != $"{ipAddress}/{cidr}")
                            {
                                var (delSuccess, delError) = await _processRunner.RunCommandAsync(new[] { "ip", "addr", "del", existingIp, "dev", "eth1" });
                                if (delSuccess) _logger.LogInformation("Removed residual IP {Ip} from eth1", existingIp);
                                else _logger.LogWarning("Failed to remove residual IP {Ip}: {Error}", existingIp, delError);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during residual IP cleanup");
            }

            // Cleanup Static Leases that are out of subnet
            try
            {
                var staticLeasesFile = "/etc/dnsmasq.d/02-static-leases.conf";
                if (File.Exists(staticLeasesFile))
                {
                    var lines = (await File.ReadAllLinesAsync(staticLeasesFile)).ToList();
                    var newLines = new List<string>();
                    bool modified = false;

                    // Calculate network range roughly
                    // Simple check: does the first 3 octets match? (Only works for /24)
                    // Better: Parse IP and check masking. For now, assuming /24 or /16 based on subnetMask string.
                    
                    var subnetBytes = IPAddress.Parse(subnetMask).GetAddressBytes();
                    var newIpBytes = IPAddress.Parse(ipAddress).GetAddressBytes();

                    foreach (var line in lines)
                    {
                        if (line.Trim().StartsWith("dhcp-host="))
                        {
                            var parts = line.Split(',');
                            string? leaseIp = null;
                            foreach(var p in parts)
                            {
                                if (IPAddress.TryParse(p.Trim(), out var parsed)) 
                                {
                                    leaseIp = p.Trim();
                                    break;
                                }
                            }

                            if (leaseIp != null)
                            {
                                var leaseBytes = IPAddress.Parse(leaseIp).GetAddressBytes();
                                bool inSubnet = true;
                                for(int i=0; i<4; i++)
                                {
                                    if ((leaseBytes[i] & subnetBytes[i]) != (newIpBytes[i] & subnetBytes[i]))
                                    {
                                        inSubnet = false;
                                        break;
                                    }
                                }

                                if (!inSubnet)
                                {
                                    _logger.LogInformation("Removing static lease {Ip} as it is outside new subnet", leaseIp);
                                    modified = true;
                                    continue; // Skip adding this line
                                }
                            }
                        }
                        newLines.Add(line);
                    }

                    if (modified)
                    {
                        await File.WriteAllLinesAsync(staticLeasesFile, newLines);
                    }
                }
            }
            catch (Exception ex)
            {
                 _logger.LogWarning(ex, "Error cleaning static leases");
            }

            // Update DHCP range to match new subnet (default start .10 to .200 for /24)
            try
            {
                string dhcpStart = null!;
                string dhcpEnd = null!;
                var parts = ipAddress.Split('.').Select(int.Parse).ToArray();
                if (cidr >= 24)
                {
                    dhcpStart = $"{parts[0]}.{parts[1]}.{parts[2]}.10";
                    dhcpEnd = $"{parts[0]}.{parts[1]}.{parts[2]}.200";
                }
                else if (cidr >= 16)
                {
                    dhcpStart = $"{parts[0]}.{parts[1]}.0.10";
                    dhcpEnd = $"{parts[0]}.{parts[1]}.255.200";
                }
                else
                {
                    dhcpStart = $"{parts[0]}.0.0.10";
                    dhcpEnd = $"{parts[0]}.255.255.200";
                }

                var dhcpContent = $"interface=eth1\ndhcp-range={dhcpStart},{dhcpEnd},12h\n";
                await File.WriteAllTextAsync(DHCP_CONFIG_FILE, dhcpContent);
                _logger.LogInformation("Updated DHCP config {File} to range {Start}-{End}", DHCP_CONFIG_FILE, dhcpStart, dhcpEnd);

                // Stop dnsmasq, clear leases, restart dnsmasq to make sure clients pick up new range
                var (stopSuccess, stopOut) = await _processRunner.RunCommandAsync(new[] { "/etc/init.d/dnsmasq", "stop" });
                if (!stopSuccess)
                {
                    stopSuccess = (await _processRunner.RunCommandAsync(new[] { "service", "dnsmasq", "stop" })).Item1;
                }

                // Remove old leases file
                try
                {
                    var leaseFile = "/var/lib/misc/dnsmasq.leases";
                    if (File.Exists(leaseFile)) File.Delete(leaseFile);
                }
                catch { }

                var (startSuccess, startOut) = await _processRunner.RunCommandAsync(new[] { "/etc/init.d/dnsmasq", "start" });
                if (!startSuccess)
                {
                    startSuccess = (await _processRunner.RunCommandAsync(new[] { "service", "dnsmasq", "start" })).Item1;
                }

                await _processRunner.RunCommandAsync(new[] { "killall", "-USR1", "dnsmasq" });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update DHCP range for new eth1 IP");
            }

            // Reload dnsmasq for changes to take effect if it's acting as a server
            await _processRunner.RunCommandAsync(new[] { "systemctl", "restart", "dnsmasq" });

            return new { success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting eth1 IP config");
            return new { success = false, error = ex.Message };
        }
    }

    private string CidrToSubnetMask(int cidr)
    {
        uint ip = 0;
        for (int i = 0; i < cidr; i++) ip = (ip >> 1) | 0x80000000;
        return new IPAddress(BitConverter.GetBytes(ip)).ToString();
    }

    private int SubnetMaskToCidr(string subnetMask)
    {
        try
        {
            var ip = IPAddress.Parse(subnetMask);
            byte[] bytes = ip.GetAddressBytes();
            int cidr = 0;
            foreach (byte b in bytes)
            {
                for (int i = 7; i >= 0; i--)
                {
                    if (((b >> i) & 1) == 1) cidr++;
                    else return cidr; // Found a 0, rest must be 0
                }
            }
            return cidr;
        }
        catch
        {
            return -1; // Invalid mask
        }
    }
}