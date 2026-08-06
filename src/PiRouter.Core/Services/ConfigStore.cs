using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiRouter.Core.Configuration;
using PiRouter.Core.Models;

namespace PiRouter.Core.Services;

public interface IConfigStore
{
    RouterConfig Current { get; }
    Task<RouterConfig> ReloadAsync(CancellationToken ct = default);
    Task MutateAsync(Action<RouterConfig> mutate, CancellationToken ct = default);

    /// <summary>Raised after any successful mutation so the reconciler can react immediately.</summary>
    event Action? Changed;
}

/// <summary>
/// Owns the persisted config file. Reads are served from memory; writes are serialised and
/// written atomically via a temp file so a crash mid-write cannot leave a truncated config
/// that would silently reset the user's bypass list on next boot.
/// </summary>
public sealed class ConfigStore : IConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly ILogger<ConfigStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RouterConfig _current = new();

    public event Action? Changed;

    public ConfigStore(IOptions<RouterOptions> options, ILogger<ConfigStore> logger)
    {
        _logger = logger;
        _path = options.Value.ConfigFilePath;

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        _current = Load();
    }

    public RouterConfig Current => _current;

    public async Task<RouterConfig> ReloadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _current = Load();
            return _current;
        }
        finally { _gate.Release(); }
    }

    public async Task MutateAsync(Action<RouterConfig> mutate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        await _gate.WaitAsync(ct);
        try
        {
            var config = Load();
            mutate(config);
            await SaveAsync(config, ct);
            _current = config;
        }
        finally { _gate.Release(); }

        Changed?.Invoke();
    }

    private RouterConfig Load()
    {
        try
        {
            if (!File.Exists(_path)) return new RouterConfig();
            var json = File.ReadAllText(_path);
            return string.IsNullOrWhiteSpace(json)
                ? new RouterConfig()
                : JsonSerializer.Deserialize<RouterConfig>(json, JsonOptions) ?? new RouterConfig();
        }
        catch (Exception ex)
        {
            // Never throw here: a corrupt config must not stop the router from booting and
            // routing traffic. Log loudly and carry on with defaults.
            _logger.LogError(ex, "Could not read {Path}; continuing with an empty configuration", _path);
            return new RouterConfig();
        }
    }

    private async Task SaveAsync(RouterConfig config, CancellationToken ct)
    {
        var temp = _path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(config, JsonOptions), ct);
        File.Move(temp, _path, overwrite: true);
        _logger.LogDebug("Saved configuration to {Path}", _path);
    }
}
