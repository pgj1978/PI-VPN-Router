using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiRouter.Core.Configuration;
using PiRouter.Core.Process;

namespace PiRouter.Core.Services;

public sealed record VpnProfile(string Name, string? Endpoint, string? Dns, int? Mtu, bool Active);

public sealed record VpnOperationResult(bool Success, string? Error, string Log);

public interface IVpnService
{
    Task<WireGuardStatus> GetStatusAsync(CancellationToken ct = default);
    Task<IReadOnlyList<VpnProfile>> ListProfilesAsync(CancellationToken ct = default);
    Task<VpnOperationResult> ConnectAsync(string profileName, CancellationToken ct = default);
    Task<VpnOperationResult> DisconnectAsync(CancellationToken ct = default);
    Task SaveProfileAsync(string name, string content, CancellationToken ct = default);
    Task DeleteProfileAsync(string name, CancellationToken ct = default);
    string? ReadEndpointHost(string profileName);
}

/// <summary>
/// Brings WireGuard profiles up and down. Deliberately knows nothing about firewall rules —
/// it changes the tunnel, the reconciler notices and rebuilds routing to match. Keeping
/// those two concerns apart is why a reconnect can no longer silently drop bypass rules.
/// </summary>
public sealed class VpnService(
    IProcessRunner runner,
    IOptions<RouterOptions> options,
    ILogger<VpnService> logger) : IVpnService
{
    private readonly RouterOptions _options = options.Value;

    public async Task<WireGuardStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var result = await runner.RunAsync(
            ["wg", "show", _options.VpnInterface, "dump"], allowFailure: true, ct: ct);

        return result.Success && !string.IsNullOrWhiteSpace(result.Stdout)
            ? WireGuardStatus.ParseDump(_options.VpnInterface, result.Stdout)
            : WireGuardStatus.Down(_options.VpnInterface);
    }

    public async Task<IReadOnlyList<VpnProfile>> ListProfilesAsync(CancellationToken ct = default)
    {
        var directory = _options.VpnProfilesDirectory;
        if (!Directory.Exists(directory)) return [];

        var status = await GetStatusAsync(ct);
        var activeEndpoint = status.PrimaryPeer?.Endpoint;

        var profiles = new List<VpnProfile>();
        foreach (var file in Directory.GetFiles(directory, "*.conf").OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var (endpoint, dns, mtu) = ParseProfile(file);

            // A profile is "active" when its endpoint host matches the peer we are actually
            // connected to. Reading it from the live tunnel rather than from a saved name
            // means the UI cannot claim a connection that isn't there.
            var active = status.Up && endpoint is not null && activeEndpoint is not null
                         && EndpointsMatch(endpoint, activeEndpoint);

            profiles.Add(new VpnProfile(name, endpoint, dns, mtu, active));
        }
        return profiles;
    }

    public async Task<VpnOperationResult> ConnectAsync(string profileName, CancellationToken ct = default)
    {
        string profilePath;
        try
        {
            profilePath = VpnProfileName.ResolvePath(_options.VpnProfilesDirectory, profileName);
        }
        catch (ArgumentException ex)
        {
            return new VpnOperationResult(false, ex.Message, string.Empty);
        }

        if (!File.Exists(profilePath))
            return new VpnOperationResult(false, $"Profile not found: {profileName}", string.Empty);

        var log = new List<string>();
        logger.LogInformation("Connecting VPN profile {Profile}", profileName);

        // Tear down whatever is up first. wg-quick down fails when nothing is up, which is fine.
        var down = await runner.RunAsync(["wg-quick", "down", _options.VpnInterface],
            allowFailure: true, timeout: TimeSpan.FromSeconds(30), ct: ct);
        log.Add($"$ wg-quick down {_options.VpnInterface}\n{down.Output}");

        try
        {
            Directory.CreateDirectory(_options.WireGuardDirectory);
            var target = Path.Combine(_options.WireGuardDirectory, $"{_options.VpnInterface}.conf");

            await File.WriteAllTextAsync(target, StageConfig(await File.ReadAllTextAsync(profilePath, ct)), ct);

            // The config holds a private key. wg-quick refuses group/world-readable configs.
            RestrictToOwner(target);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not stage the WireGuard config for {Profile}", profileName);
            return new VpnOperationResult(false, $"Could not write config: {ex.Message}", string.Join('\n', log));
        }

        var up = await runner.RunAsync(["wg-quick", "up", _options.VpnInterface],
            timeout: TimeSpan.FromSeconds(60), ct: ct);
        log.Add($"$ wg-quick up {_options.VpnInterface}\n{up.Output}");

        if (!up.Success)
        {
            // Overwhelmingly the most common cause is DNS: wg-quick cannot resolve the
            // endpoint hostname. Say so rather than surfacing a bare exit code.
            var hint = up.Output.Contains("resolve", StringComparison.OrdinalIgnoreCase)
                ? " (the endpoint hostname could not be resolved - check DNS on the Diagnostics page)"
                : string.Empty;

            logger.LogError("wg-quick up failed for {Profile}: {Error}", profileName, up.Output);
            return new VpnOperationResult(false, $"Failed to bring up the tunnel{hint}: {up.Output}", string.Join('\n', log));
        }

        logger.LogInformation("VPN profile {Profile} is up", profileName);
        return new VpnOperationResult(true, null, string.Join('\n', log));
    }

    public async Task<VpnOperationResult> DisconnectAsync(CancellationToken ct = default)
    {
        var result = await runner.RunAsync(["wg-quick", "down", _options.VpnInterface],
            allowFailure: true, timeout: TimeSpan.FromSeconds(30), ct: ct);

        logger.LogInformation("VPN disconnected");
        return new VpnOperationResult(true, null, result.Output);
    }

    public async Task SaveProfileAsync(string name, string content, CancellationToken ct = default)
    {
        var path = VpnProfileName.ResolvePath(_options.VpnProfilesDirectory, name);
        Directory.CreateDirectory(_options.VpnProfilesDirectory);
        await File.WriteAllTextAsync(path, content, ct);
        RestrictToOwner(path);
        logger.LogInformation("Saved VPN profile {Profile}", name);
    }

    public Task DeleteProfileAsync(string name, CancellationToken ct = default)
    {
        var path = VpnProfileName.ResolvePath(_options.VpnProfilesDirectory, name);
        if (File.Exists(path)) File.Delete(path);
        logger.LogInformation("Deleted VPN profile {Profile}", name);
        return Task.CompletedTask;
    }

    /// <summary>Hostname portion of a profile's Endpoint, used by the DNS diagnostic.</summary>
    public string? ReadEndpointHost(string profileName)
    {
        try
        {
            var path = VpnProfileName.ResolvePath(_options.VpnProfilesDirectory, profileName);
            if (!File.Exists(path)) return null;
            var (endpoint, _, _) = ParseProfile(path);
            return endpoint is null ? null : HostOf(endpoint);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Prepares a profile for wg-quick, dropping any <c>DNS =</c> line.
    ///
    /// Two reasons, and the second is the important one:
    ///
    /// 1. A DNS line makes wg-quick shell out to resolvconf, which cannot work inside a
    ///    container — no init system, and /etc/resolv.conf belongs to Docker. wg-quick
    ///    treats that failure as fatal and tears the interface straight back down, so the
    ///    tunnel never comes up at all.
    ///
    /// 2. We do not want it to succeed even where it could. dnsmasq owns DNS for this
    ///    network with upstreams set explicitly in its own config. Letting a VPN profile
    ///    rewrite the host's resolv.conf hands name resolution to a third party's servers
    ///    behind everyone's back — and a resolver in that file quietly becoming unreachable
    ///    is precisely what took this router's tunnel, and every LAN client's DNS, down
    ///    before.
    /// </summary>
    internal static string StageConfig(string profileContent)
    {
        var kept = profileContent
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("DNS", StringComparison.OrdinalIgnoreCase)
                           || !line.Contains('='))
            .Select(line => line.TrimEnd('\r'));

        return string.Join('\n', kept).TrimEnd() + "\n";
    }

    /// <summary>
    /// Pulls the interesting fields out of a wg config. Never returns the private key —
    /// nothing outside this file should be able to make it reach an API response.
    /// </summary>
    private static (string? Endpoint, string? Dns, int? Mtu) ParseProfile(string path)
    {
        string? endpoint = null, dns = null;
        int? mtu = null;

        try
        {
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('[')) continue;

                var separator = line.IndexOf('=');
                if (separator < 0) continue;

                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();

                if (key.Equals("Endpoint", StringComparison.OrdinalIgnoreCase)) endpoint = value;
                else if (key.Equals("DNS", StringComparison.OrdinalIgnoreCase)) dns = value;
                else if (key.Equals("MTU", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var m)) mtu = m;
            }
        }
        catch (IOException)
        {
            // An unreadable profile is reported as having no metadata rather than failing the list.
        }

        return (endpoint, dns, mtu);
    }

    /// <summary>
    /// Locks a file down to owner read/write. WireGuard configs contain a private key and
    /// wg-quick refuses to load one that is group- or world-readable. No-ops off Unix so the
    /// project still runs on a Windows dev machine.
    /// </summary>
    private static void RestrictToOwner(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static string HostOf(string endpoint)
    {
        var colon = endpoint.LastIndexOf(':');
        return colon > 0 ? endpoint[..colon] : endpoint;
    }

    /// <summary>
    /// The live peer endpoint is an IP:port while the profile holds a hostname:port, so
    /// they are compared on port plus resolved address rather than as strings.
    /// </summary>
    private static bool EndpointsMatch(string profileEndpoint, string liveEndpoint)
    {
        if (string.Equals(profileEndpoint, liveEndpoint, StringComparison.OrdinalIgnoreCase)) return true;

        var host = HostOf(profileEndpoint);
        var liveHost = HostOf(liveEndpoint);
        if (string.Equals(host, liveHost, StringComparison.OrdinalIgnoreCase)) return true;

        try
        {
            return System.Net.Dns.GetHostAddresses(host)
                .Any(a => a.ToString() == liveHost);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
        {
            return false;
        }
    }
}
