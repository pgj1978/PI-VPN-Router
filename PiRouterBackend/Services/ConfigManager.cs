using System.Text.Json;
using PiRouterBackend.Models;

namespace PiRouterBackend.Services;

public interface IConfigManager
{
    RouterConfig LoadConfig();
    Task SaveConfigAsync(RouterConfig config);
    string GetVpnProfilesDir();
    string GetConfigFilePath();
    string GetWireGuardDir();
}

public class ConfigManager : IConfigManager
{
    private readonly string _vpnProfilesDir;
    private readonly string _configFile;
    private readonly string _wireGuardDir;
    private readonly ILogger<ConfigManager> _logger;
    private readonly string _userHome;

    public ConfigManager(ILogger<ConfigManager> logger)
    {
        _logger = logger;
        _userHome = Environment.GetEnvironmentVariable("HOME") ?? "/home/pgj99";

        if (Directory.Exists("/app"))
        {
            _vpnProfilesDir = "/app/config/vpn_profiles";
            _configFile = "/app/config/router_config.json";
        }
        else
        {
            _vpnProfilesDir = Path.Combine(_userHome, "code/PiRouter/wireguard_configs");
            _configFile = Path.Combine(_userHome, "code/PiRouter/backend/config/router_config.json");
        }

        _wireGuardDir = "/etc/wireguard";

        // Ensure directories exist
        Directory.CreateDirectory(Path.GetDirectoryName(_vpnProfilesDir)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_configFile)!);
    }

    public RouterConfig LoadConfig()
    {
        try
        {
            if (File.Exists(_configFile))
            {
                var json = File.ReadAllText(_configFile);
                var config = JsonSerializer.Deserialize<RouterConfig>(json);
                return config ?? new RouterConfig();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading config");
        }

        return new RouterConfig();
    }

    public async Task SaveConfigAsync(RouterConfig config)
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(config, options);
            await File.WriteAllTextAsync(_configFile, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving config");
            throw;
        }
    }

    public string GetVpnProfilesDir() => _vpnProfilesDir;
    public string GetConfigFilePath() => _configFile;
    public string GetWireGuardDir() => _wireGuardDir;
}
