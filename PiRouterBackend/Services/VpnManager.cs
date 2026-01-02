using PiRouterBackend.Models;

namespace PiRouterBackend.Services;

public interface IVpnManager
{
    Task Initialize();
    Task<object> ListVpnConfigs();
    Task<object> GetVpnStatus();
    Task<object> ConnectVpn(string profileName);
    Task<object> DisconnectVpn();
    Task<object> ToggleKillSwitch(bool enabled);
    Task<object> AddVpnProfile(string name, string configContent);
    Task<object> DeleteVpnProfile(string name);
}

public class VpnManager : IVpnManager
{
    private readonly IProcessRunner _processRunner;
    private readonly IConfigManager _configManager;
    private readonly ILogger<VpnManager> _logger;
    private const string WG_INTERFACE = "wg0";

    public VpnManager(IProcessRunner processRunner, IConfigManager configManager, ILogger<VpnManager> logger)
    {
        _processRunner = processRunner;
        _configManager = configManager;
        _logger = logger;
    }

    public async Task Initialize()
    {
        try
        {
            _logger.LogInformation("Initializing VpnManager...");
            var config = _configManager.LoadConfig();

            if (!string.IsNullOrEmpty(config.ActiveVpn))
            {
                _logger.LogInformation("Found active VPN profile in config: {Profile}", config.ActiveVpn);

                // Check if already running
                var (success, output) = await _processRunner.RunCommandAsync(new[] { "wg", "show", WG_INTERFACE }, useSudo: false);
                if (success && !string.IsNullOrWhiteSpace(output))
                {
                    _logger.LogInformation("VPN interface {Interface} is already up. Ensuring routing exceptions are applied.", WG_INTERFACE);
                    await ApplyRoutingExceptions();
                    return;
                }

                _logger.LogInformation("Restoring VPN connection to {Profile}...", config.ActiveVpn);
                
                // Attempt to connect
                var result = await ConnectVpn(config.ActiveVpn);
                // We don't really do anything with the result object here, but logs will show what happened
            }
            else
            {
                 _logger.LogInformation("No active VPN profile configured.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing VPN manager");
        }
    }

    public async Task<object> ListVpnConfigs()
    {
        try
        {
            var vpnDir = _configManager.GetVpnProfilesDir();
            if (!Directory.Exists(vpnDir))
            {
                return new { configs = new List<WireGuardConfig>() };
            }

            var configs = Directory.GetFiles(vpnDir, "*.conf")
                .Select(f => new WireGuardConfig
                {
                    Name = Path.GetFileNameWithoutExtension(f),
                    Filename = Path.GetFileName(f),
                    Active = false
                })
                .ToList();

            // Check which one is active by loading config and checking status
            var routerConfig = _configManager.LoadConfig();
            if (!string.IsNullOrEmpty(routerConfig.ActiveVpn))
            {
                // Also verify it's actually connected
                var (success, output) = await _processRunner.RunCommandAsync(new[] { "wg", "show", "wg0" }, useSudo: false);
                if (success && !string.IsNullOrWhiteSpace(output))
                {
                    var activeProfile = routerConfig.ActiveVpn;
                    foreach (var config in configs)
                    {
                        config.Active = config.Name == activeProfile;
                    }
                }
            }

            var killSwitchEnabled = routerConfig.KillSwitchEnabled;
            return new { configs, kill_switch_enabled = killSwitchEnabled };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing VPN configs");
            throw;
        }
    }

    public async Task<object> GetVpnStatus()
    {
        try
        {
            var (success, output) = await _processRunner.RunCommandAsync(new[] { "wg", "show", WG_INTERFACE }, useSudo: false);
            
            var config = _configManager.LoadConfig();
            var connected = success && !string.IsNullOrWhiteSpace(output);

            return new
            {
                connected,
                profile = config.ActiveVpn,
                interface_name = WG_INTERFACE
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting VPN status");
            return new { connected = false, profile = null as string, error = ex.Message };
        }
    }

    private async Task ApplyDeviceRouting(string mac, bool bypass)
    {
        try
        {
            _logger.LogInformation("Applying routing for {Mac}, bypass: {Bypass}", mac, bypass);
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying device routing");
        }
    }

    private async Task<bool> CleanupPreviousState()
    {
        try
        {
            _logger.LogInformation("Performing cleanup of previous VPN states...");

            // Step 1: Clean up any active interfaces
            var (success, output) = await _processRunner.RunCommandAsync(new[] { "wg", "show", "interfaces" }, useSudo: false);
            
            if (success && !string.IsNullOrWhiteSpace(output))
            {
                var interfaces = output.Trim().Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var iface in interfaces)
                {
                    _logger.LogInformation("Found active interface: {Interface}. Shutting down...", iface);
                    
                    // Try standard wg-quick down
                    var (downSuccess, downError) = await _processRunner.RunCommandAsync(
                        new[] { "wg-quick", "down", iface }, 
                        useSudo: false
                    );
                    
                    // If wg-quick fails, force delete
                    if (!downSuccess)
                    {
                        _logger.LogWarning("wg-quick down failed for {Interface}. Forcing link delete.", iface);
                        await _processRunner.RunCommandAsync(
                            new[] { "ip", "link", "delete", iface }, 
                            useSudo: false
                        );
                    }
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during cleanup of previous state");
            return false;
        }
    }

    public async Task<object> ConnectVpn(string profileName)
    {
        try
        {
            _logger.LogInformation("Connecting to VPN profile: {Profile}", profileName);

            var vpnDir = _configManager.GetVpnProfilesDir();
            var profilePath = Path.Combine(vpnDir, $"{profileName}.conf");

            if (!File.Exists(profilePath))
            {
                return new { success = false, error = $"Profile not found: {profileName}" };
            }

            // 1. Aggressive cleanup of any previous VPN state
            await CleanupPreviousState();

            // 2. Copy profile to /etc/wireguard/wg0.conf
            var wgDir = _configManager.GetWireGuardDir();
            var wgConfPath = Path.Combine(wgDir, $"{WG_INTERFACE}.conf");

            var profileContent = File.ReadAllText(profilePath);
            
            // Write config file directly
            try
            {
                File.WriteAllText(wgConfPath, profileContent);
            }
            catch (Exception writeEx)
            {
                _logger.LogError(writeEx, "Failed to write WireGuard config");
                return new { success = false, error = $"Failed to write config: {writeEx.Message}" };
            }

            // 3. Bring up the interface
            var (upSuccess, upOutput) = await _processRunner.RunCommandAsync(
                new[] { "wg-quick", "up", WG_INTERFACE },
                useSudo: false
            );

            if (!upSuccess)
            {
                return new { success = false, error = $"Failed to bring up interface: {upOutput}", logs = upOutput };
            }

            // 3.5 Apply Local Routing Exceptions
            // Prevents local LAN and Docker traffic from being captured by wg-quick's routing table (51820)
            await ApplyRoutingExceptions();

            // 4. Update config
            var routerConfig = _configManager.LoadConfig();
            routerConfig.ActiveVpn = profileName;
            await _configManager.SaveConfigAsync(routerConfig);

            return new { success = true, profile = profileName, logs = upOutput };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error connecting to VPN");
            return new { success = false, error = ex.Message, logs = ex.ToString() };
        }
    }

    public async Task<object> DisconnectVpn()
    {
        try
        {
            _logger.LogInformation("Disconnecting VPN");

            var (success, output) = await _processRunner.RunCommandAsync(
                new[] { "wg-quick", "down", WG_INTERFACE },
                useSudo: true
            );

            if (!success)
            {
                return new { success = false, error = output };
            }

            var routerConfig = _configManager.LoadConfig();
            routerConfig.ActiveVpn = null;
            await _configManager.SaveConfigAsync(routerConfig);

            return new { success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disconnecting VPN");
            return new { success = false, error = ex.Message };
        }
    }

    public async Task<object> ToggleKillSwitch(bool enabled)
    {
        try
        {
            var routerConfig = _configManager.LoadConfig();
            routerConfig.KillSwitchEnabled = enabled;
            await _configManager.SaveConfigAsync(routerConfig);

            _logger.LogInformation("Kill switch set to: {Enabled}", enabled);
            return new { success = true, kill_switch_enabled = enabled };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error toggling kill switch");
            return new { success = false, error = ex.Message };
        }
    }

    public async Task<object> AddVpnProfile(string name, string configContent)
    {
        try
        {
            var vpnDir = _configManager.GetVpnProfilesDir();
            var profilePath = Path.Combine(vpnDir, $"{name}.conf");

            if (File.Exists(profilePath))
            {
                return new { success = false, error = "Profile already exists" };
            }

            await File.WriteAllTextAsync(profilePath, configContent);
            _logger.LogInformation("Added VPN profile: {Profile}", name);

            return new { success = true, profile = name };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding VPN profile");
            return new { success = false, error = ex.Message };
        }
    }

    public async Task<object> DeleteVpnProfile(string name)
    {
        try
        {
            var vpnDir = _configManager.GetVpnProfilesDir();
            var profilePath = Path.Combine(vpnDir, $"{name}.conf");

            if (!File.Exists(profilePath))
            {
                return await Task.FromResult(new { success = false, error = "Profile not found" });
            }

            File.Delete(profilePath);
            _logger.LogInformation("Deleted VPN profile: {Profile}", name);

            return await Task.FromResult(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting VPN profile");
            return new { success = false, error = ex.Message };
        }
    }

    private async Task<string> GetWireGuardTableId()
    {
        try 
        {
            // Use sudo because wg usually requires it
            var (success, output) = await _processRunner.RunCommandAsync(new[] { "wg", "show", WG_INTERFACE, "fwmark" }, useSudo: true);
            if (success && !string.IsNullOrWhiteSpace(output))
            {
                var val = output.Trim();
                if (val.Equals("off", StringComparison.OrdinalIgnoreCase)) return "51820";
                
                if (val.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    try 
                    {
                        return Convert.ToInt32(val, 16).ToString();
                    }
                    catch 
                    {
                        _logger.LogWarning("Failed to parse fwmark hex: {Value}", val);
                    }
                }
                if (int.TryParse(val, out _)) return val;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to determine WireGuard table ID, defaulting to 51820");
        }
        return "51820";
    }

    private async Task ApplyRoutingExceptions()
    {
        try
        {
            var tableId = await GetWireGuardTableId();
            _logger.LogInformation("Applying routing exceptions for local networks to Table {TableId}...", tableId);
            
            // Exclude Docker ranges (172.16.0.0/12 covers 172.16-172.31)
            await _processRunner.RunCommandAsync(new[] { "ip", "route", "add", "throw", "172.16.0.0/12", "table", tableId }, logFailure: false);
            // Exclude LAN ranges
            await _processRunner.RunCommandAsync(new[] { "ip", "route", "add", "throw", "192.168.0.0/16", "table", tableId }, logFailure: false);

            // Shift WireGuard rule priority to 20000 to allow Bypass (Priority 1) to take precedence
            // wg-quick adds: not from all fwmark <mark> lookup <tableId> (default priority 0)
            // We want:       not from all fwmark <mark> lookup <tableId> priority 20000
            
            _logger.LogInformation("Shifting WireGuard rule priority to 20000 to allow bypass...");
            
            // Add lower priority rule first
            await _processRunner.RunCommandAsync(new[] { 
                "ip", "rule", "add", "not", "fwmark", tableId, "lookup", tableId, "priority", "20000" 
            }, logFailure: false);

            // Remove default high priority rule (priority 0)
            await _processRunner.RunCommandAsync(new[] { 
                "ip", "rule", "del", "not", "fwmark", tableId, "lookup", tableId, "priority", "0" 
            }, logFailure: false);

            // Fix MTU/MSS issues for clients routed through VPN
            // Explicitly set MSS to 1360 (safe for WireGuard 1420 MTU) to prevent black holes
            // Apply to multiple chains to ensure coverage for all traffic patterns
            _logger.LogInformation("Applying explicit TCP MSS clamping to 1360 on all relevant chains...");
            
            // FORWARD chain - for traffic passing through the router (LAN devices via VPN)
            await _processRunner.RunCommandAsync(new[] {
                "iptables", "-t", "mangle", "-D", "FORWARD", "-p", "tcp", "--tcp-flags", "SYN,RST", "SYN", "-j", "TCPMSS", "--set-mss", "1360"
            }, logFailure: false);
            await _processRunner.RunCommandAsync(new[] {
                "iptables", "-t", "mangle", "-I", "FORWARD", "-p", "tcp", "--tcp-flags", "SYN,RST", "SYN", "-j", "TCPMSS", "--set-mss", "1360"
            });

            // OUTPUT chain - for traffic originated from Pi itself
            await _processRunner.RunCommandAsync(new[] {
                "iptables", "-t", "mangle", "-D", "OUTPUT", "-p", "tcp", "--tcp-flags", "SYN,RST", "SYN", "-j", "TCPMSS", "--set-mss", "1360"
            }, logFailure: false);
            await _processRunner.RunCommandAsync(new[] {
                "iptables", "-t", "mangle", "-I", "OUTPUT", "-p", "tcp", "--tcp-flags", "SYN,RST", "SYN", "-j", "TCPMSS", "--set-mss", "1360"
            });

            // POSTROUTING chain - for return traffic (ensures symmetry)
            await _processRunner.RunCommandAsync(new[] {
                "iptables", "-t", "mangle", "-D", "POSTROUTING", "-p", "tcp", "--tcp-flags", "SYN,RST", "SYN", "-j", "TCPMSS", "--set-mss", "1360"
            }, logFailure: false);
            await _processRunner.RunCommandAsync(new[] {
                "iptables", "-t", "mangle", "-I", "POSTROUTING", "-p", "tcp", "--tcp-flags", "SYN,RST", "SYN", "-j", "TCPMSS", "--set-mss", "1360"
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply routing exceptions");
        }
    }
}
