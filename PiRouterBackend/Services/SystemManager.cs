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
            // This is a simplified approach. A robust solution would parse/modify /etc/dhcpcd.conf or similar.
            // For now, this will apply the IP temporarily and restart networking.
            // Persistence across reboots would need file modification.

            int cidr = SubnetMaskToCidr(subnetMask);
            if (cidr == -1)
            {
                return new { success = false, error = "Invalid subnet mask" };
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

            // This should restart networking for eth1, but may need a more general restart
            // For persistence, /etc/dhcpcd.conf needs to be updated.
            // For now, these changes are not persistent. This is a known limitation.
            
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