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
    Task RebootSystem();
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

    public async Task RebootSystem()
    {
        _logger.LogWarning("Initiating system reboot via API request");
        // Use nsenter to run reboot on the host
        await _processRunner.RunCommandAsync(new[] { 
            "nsenter", "-t", "1", "-m", "-u", "-n", "-i", "/sbin/reboot" 
        }, logFailure: true);
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
            
            // Handle case where lease time is in the dhcp-range line: dhcp-range=...,...,12h
            string? leaseTime = null;
            if (leaseTimeMatch.Success)
            {
                leaseTime = leaseTimeMatch.Groups["leaseTime"].Value;
            }
            else
            {
                 // Try to find it in the dhcp-range line
                 var rangeLineMatch = Regex.Match(content, @"dhcp-range=[\d\.]+,[\d\.]+,([^\n]+)");
                 if (rangeLineMatch.Success)
                 {
                     leaseTime = rangeLineMatch.Groups[1].Value.Trim();
                 }
            }

            return new
            {
                enabled = dhcpRangeMatch.Success,
                dhcpRange = dhcpRangeMatch.Success ? $"{dhcpRangeMatch.Groups["startIp"].Value},{dhcpRangeMatch.Groups["endIp"].Value}" : null,
                leaseTime = leaseTime
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
            bool success = await RunHostServiceCommand("dnsmasq", "restart");
            if (!success)
            {
                return new { success = false, error = "Failed to reload dnsmasq service" };
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

            // Retrieve existing lease time before we potentially overwrite the file
            string currentLeaseTime = "12h"; // Default
            try 
            {
                if (File.Exists(DHCP_CONFIG_FILE))
                {
                    var content = await File.ReadAllTextAsync(DHCP_CONFIG_FILE);
                    var rangeLineMatch = Regex.Match(content, @"dhcp-range=[\d\.]+,[\d\.]+,([^\n]+)");
                    if (rangeLineMatch.Success)
                    {
                        currentLeaseTime = rangeLineMatch.Groups[1].Value.Trim();
                    }
                }
            } catch {}

            // Flush old address
            await _processRunner.RunCommandAsync(new[] { "ip", "addr", "flush", "dev", "eth1" });
            
            // Add new address
            var (success, output) = await _processRunner.RunCommandAsync(new[] { "ip", "addr", "add", $"{ipAddress}/{cidr}", "dev", "eth1" });
            if (!success)
            {
                _logger.LogError("Failed to set IP address for eth1: {Output}", output);
                return new { success = false, error = $"Failed to set IP address: {output}" };
            }

            // Safety cleanup: Ensure no other IPs remain
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
                            if (existingIp != $"{ipAddress}/{cidr}")
                            {
                                var (delSuccess, delError) = await _processRunner.RunCommandAsync(new[] { "ip", "addr", "del", existingIp, "dev", "eth1" });
                                if (delSuccess) _logger.LogInformation("Removed residual IP {Ip} from eth1", existingIp);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during residual IP cleanup");
            }

            // Clean up hardcoded gateway config if it exists
            try
            {
                var gatewayFile = "/etc/dnsmasq.d/eth1.conf";
                if (File.Exists(gatewayFile))
                {
                    File.Delete(gatewayFile);
                    _logger.LogInformation("Deleted hardcoded gateway config {File}", gatewayFile);
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Error removing hardcoded gateway config"); }

            // Cleanup Static Leases that are out of subnet
            try
            {
                var staticLeasesFile = "/etc/dnsmasq.d/02-static-leases.conf";
                if (File.Exists(staticLeasesFile))
                {
                    var lines = (await File.ReadAllLinesAsync(staticLeasesFile)).ToList();
                    var newLines = new List<string>();
                    bool modified = false;
                    
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
                                    continue;
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

            // Persist the IP
            // Priority 1: systemd-networkd
            try
            {
                var systemdNetworkFile = "/etc/systemd/network/10-eth1.network";
                if (File.Exists(systemdNetworkFile))
                {
                    var content = $"[Match]\nName=eth1\n\n[Network]\nAddress={ipAddress}/{cidr}\n";
                    await File.WriteAllTextAsync(systemdNetworkFile, content);
                    _logger.LogInformation("Updated {File} with persistent eth1 IP {Ip}", systemdNetworkFile, $"{ipAddress}/{cidr}");

                    bool persistSuccess = await RunHostServiceCommand("systemd-networkd", "restart");
                    if (!persistSuccess) _logger.LogWarning("Failed to restart systemd-networkd");
                }
                // Priority 2: dhcpcd (Legacy / Raspberry Pi OS default before Bookworm)
                else
                {
                    var dhcpcdFile = ETH1_NETWORK_CONFIG_FILE;
                    var ipWithCidr = $"{ipAddress}/{cidr}";

                    if (File.Exists(dhcpcdFile))
                    {
                        var lines = (await File.ReadAllLinesAsync(dhcpcdFile)).ToList();
                        var newLines = new List<string>();
                        bool inEth1 = false;
                        bool wroteStatic = false;

                        for (int i = 0; i < lines.Count; i++)
                        {
                            var line = lines[i];
                            if (line.Trim().StartsWith("interface eth1"))
                            {
                                inEth1 = true;
                                newLines.Add(line);
                                continue;
                            }

                            if (inEth1)
                            {
                                if (line.Trim().StartsWith("interface "))
                                {
                                    if (!wroteStatic)
                                    {
                                        newLines.Add($"static ip_address={ipWithCidr}");
                                        wroteStatic = true;
                                    }
                                    inEth1 = false;
                                    newLines.Add(line);
                                    continue;
                                }

                                if (line.Trim().StartsWith("static ip_address="))
                                {
                                    if (!wroteStatic)
                                    {
                                        newLines.Add($"static ip_address={ipWithCidr}");
                                        wroteStatic = true;
                                    }
                                    continue;
                                }
                            }

                            newLines.Add(line);
                        }

                        if (!wroteStatic)
                        {
                            newLines.Add("");
                            newLines.Add("interface eth1");
                            newLines.Add($"static ip_address={ipWithCidr}");
                        }

                        await File.WriteAllLinesAsync(dhcpcdFile, newLines);
                        _logger.LogInformation("Updated {File} with persistent eth1 IP {Ip}", dhcpcdFile, ipWithCidr);

                        // Restart dhcpcd using robust method
                        await RunHostServiceCommand("dhcpcd", "restart");
                    }
                    else
                    {
                        // If file doesn't exist, create it (assuming dhcpcd system)
                        var content = $"interface eth1\nstatic ip_address={ipAddress}/{cidr}\n";
                        await File.WriteAllTextAsync(dhcpcdFile, content);
                        _logger.LogInformation("Created {File} with eth1 IP {Ip}", dhcpcdFile, ipWithCidr);
                        await RunHostServiceCommand("dhcpcd", "restart");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist eth1 IP configuration");
            }

            // Update DHCP range to match new subnet
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

                // Use preserved lease time
                var dhcpContent = $"interface=eth1\ndhcp-range={dhcpStart},{dhcpEnd},{currentLeaseTime}\n";
                await File.WriteAllTextAsync(DHCP_CONFIG_FILE, dhcpContent);
                _logger.LogInformation("Updated DHCP config {File} to range {Start}-{End} with lease {Lease}", DHCP_CONFIG_FILE, dhcpStart, dhcpEnd, currentLeaseTime);

                // Stop dnsmasq
                _logger.LogInformation("Stopping dnsmasq for lease clearing...");
                await RunHostServiceCommand("dnsmasq", "stop");
                
                // Clear leases
                try
                {
                    var leaseFile = "/var/lib/misc/dnsmasq.leases";
                    if (File.Exists(leaseFile)) 
                    {
                        File.Delete(leaseFile);
                        _logger.LogInformation("Deleted leases file {File}", leaseFile);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete leases file");
                }

                // Clear dnsmasq cache (files in /var/lib/dnsmasq/)
                try
                {
                    var dnsmasqDbDir = "/var/lib/dnsmasq";
                    if (Directory.Exists(dnsmasqDbDir))
                    {
                         var files = Directory.GetFiles(dnsmasqDbDir);
                         foreach (var file in files)
                         {
                             try { File.Delete(file); } catch { }
                         }
                         _logger.LogInformation("Cleared dnsmasq cache directory");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clear dnsmasq cache");
                }

                // Start dnsmasq
                _logger.LogInformation("Starting dnsmasq...");
                var startSuccess = await RunHostServiceCommand("dnsmasq", "start");
                if (!startSuccess)
                {
                     _logger.LogError("Failed to start dnsmasq after IP update");
                     // Try one more restart
                     await RunHostServiceCommand("dnsmasq", "restart");
                }
                
                // Signal reload just in case
                await _processRunner.RunCommandAsync(new[] { "nsenter", "-t", "1", "-m", "-u", "-n", "-i", "/usr/bin/killall", "-USR1", "dnsmasq" }, logFailure: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update DHCP range for new eth1 IP");
            }

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
        // Safe, endian-neutral implementation
        byte[] bytes = new byte[4];
        for (int i = 0; i < cidr; i++)
        {
            bytes[i / 8] |= (byte)(1 << (7 - (i % 8)));
        }
        return new IPAddress(bytes).ToString();
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

    /// <summary>
    /// Runs a service command on the host using multiple fallback methods via nsenter.
    /// Methods: init.d -> service -> systemctl
    /// </summary>
    private async Task<bool> RunHostServiceCommand(string serviceName, string action)
    {
        // Method 1: init.d (via nsenter)
        var (success, output) = await _processRunner.RunCommandAsync(
            new[] { "nsenter", "-t", "1", "-m", "-u", "-n", "-i", $"/etc/init.d/{serviceName}", action }, 
            logFailure: false
        );

        if (success) return true;
        _logger.LogDebug("Failed {Service} {Action} with init.d: {Output}", serviceName, action, output);

        // Method 2: service command (via nsenter)
        (success, output) = await _processRunner.RunCommandAsync(
            new[] { "nsenter", "-t", "1", "-m", "-u", "-n", "-i", "service", serviceName, action }, 
            logFailure: false
        );

        if (success) return true;
        _logger.LogDebug("Failed {Service} {Action} with service command: {Output}", serviceName, action, output);

        // Method 3: systemctl (via nsenter)
        (success, output) = await _processRunner.RunCommandAsync(
            new[] { "nsenter", "-t", "1", "-m", "-u", "-n", "-i", "/usr/bin/systemctl", action, serviceName }, 
            logFailure: true // Log failure on last attempt
        );

        if (!success)
        {
            _logger.LogError("Failed {Service} {Action} with all methods. Last error: {Output}", serviceName, action, output);
        }

        return success;
    }
}
