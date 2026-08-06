using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiRouter.Core.Configuration;
using PiRouter.Core.Models;
using PiRouter.Core.Net;
using PiRouter.Core.Process;

namespace PiRouter.Core.Services;

public sealed record DhcpSettings(bool Enabled, string RangeStart, string RangeEnd, string LeaseTime);

public interface IDnsmasqService
{
    Task<DhcpSettings> GetSettingsAsync(CancellationToken ct = default);
    Task ApplySettingsAsync(DhcpSettings settings, CancellationToken ct = default);

    /// <summary>Writes the config from configuration if it is missing, so a fresh stack converges on its own.</summary>
    Task EnsureConfiguredAsync(IEnumerable<DeviceConfig> devices, CancellationToken ct = default);
    Task WriteStaticLeasesAsync(IEnumerable<DeviceConfig> devices, CancellationToken ct = default);
    Task<bool> ReloadAsync(CancellationToken ct = default);
    Task<bool> IsRunningAsync(CancellationToken ct = default);
}

/// <summary>
/// Owns dnsmasq's configuration.
///
/// Static reservations live in a dhcp-hostsdir, which dnsmasq re-reads on SIGHUP. That
/// matters: changing a device's reserved address no longer requires bouncing the DHCP
/// server and briefly cutting DHCP for every other device on the LAN, which is what the
/// old stop/clear-leases/start dance did.
/// </summary>
public sealed class DnsmasqService(
    IProcessRunner runner,
    IDockerClient docker,
    IOptions<RouterOptions> options,
    ILogger<DnsmasqService> logger) : IDnsmasqService
{
    /// <summary>Container name in docker-compose.yml. Only used for full restarts.</summary>
    public const string ContainerName = "pirouter-dnsmasq";

    private readonly RouterOptions _options = options.Value;

    private string ConfigFile => Path.Combine(_options.DnsmasqConfigDirectory, "10-pirouter.conf");
    private string StaticLeaseDir => Path.Combine(_options.DnsmasqConfigDirectory, "static-leases.d");

    public async Task<DhcpSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        var defaults = new DhcpSettings(true, _options.DhcpRangeStart, _options.DhcpRangeEnd, _options.DhcpLeaseTime);
        try
        {
            if (!File.Exists(ConfigFile)) return defaults with { Enabled = false };

            foreach (var line in await File.ReadAllLinesAsync(ConfigFile, ct))
            {
                if (!line.StartsWith("dhcp-range=", StringComparison.Ordinal)) continue;

                var parts = line["dhcp-range=".Length..].Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length >= 2)
                    return new DhcpSettings(true, parts[0], parts[1],
                        parts.Length >= 3 ? parts[2] : _options.DhcpLeaseTime);
            }
            return defaults with { Enabled = false };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read dnsmasq config at {Path}", ConfigFile);
            return defaults with { Enabled = false };
        }
    }

    /// <summary>
    /// Brings dnsmasq's config into existence on first boot.
    ///
    /// Without this the container starts from the base config alone: no upstream servers and
    /// no dhcp-range, so it answers nothing and hands out no addresses. Waiting for a user to
    /// visit the DHCP settings page before the stack becomes functional is not a working
    /// deployment.
    /// </summary>
    public async Task EnsureConfiguredAsync(IEnumerable<DeviceConfig> devices, CancellationToken ct = default)
    {
        var deviceList = devices.ToList();

        if (File.Exists(ConfigFile))
        {
            logger.LogDebug("dnsmasq config already present at {Path}", ConfigFile);
            await WriteStaticLeasesAsync(deviceList, ct);
            return;
        }

        logger.LogInformation("No dnsmasq config found; writing defaults from configuration");

        await WriteConfigAsync(
            new DhcpSettings(true, _options.DhcpRangeStart, _options.DhcpRangeEnd, _options.DhcpLeaseTime), ct);

        await WriteStaticLeasesAsync(deviceList, ct);

        // The container started before this file existed, so it has to re-read it.
        if (!await docker.RestartContainerAsync(ContainerName, ct))
            await ReloadAsync(ct);
    }

    public async Task ApplySettingsAsync(DhcpSettings settings, CancellationToken ct = default)
    {
        if (settings.Enabled)
        {
            if (!Cidr.IsValidIpv4(settings.RangeStart) || !Cidr.IsValidIpv4(settings.RangeEnd))
                throw new ArgumentException("DHCP range start and end must both be valid IPv4 addresses");

            if (!Cidr.Contains(_options.LanNetwork, settings.RangeStart) ||
                !Cidr.Contains(_options.LanNetwork, settings.RangeEnd))
                throw new ArgumentException(
                    $"DHCP range {settings.RangeStart}-{settings.RangeEnd} must sit inside the LAN network {_options.LanNetwork}");
        }

        await WriteConfigAsync(settings, ct);

        // dhcp-range lives in the main config, which SIGHUP does not re-read, so this one
        // genuinely needs a restart.
        if (!await docker.RestartContainerAsync(ContainerName, ct))
            await ReloadAsync(ct);
    }

    /// <summary>
    /// Writes one file per reservation into the hosts dir. Rewriting the whole directory
    /// each time means a removed reservation actually disappears rather than lingering.
    /// </summary>
    public async Task WriteStaticLeasesAsync(IEnumerable<DeviceConfig> devices, CancellationToken ct = default)
    {
        Directory.CreateDirectory(StaticLeaseDir);

        foreach (var stale in Directory.GetFiles(StaticLeaseDir))
        {
            try { File.Delete(stale); }
            catch (IOException ex) { logger.LogDebug("Could not remove {File}: {Error}", stale, ex.Message); }
        }

        var count = 0;
        foreach (var device in devices)
        {
            if (string.IsNullOrWhiteSpace(device.StaticIp)) continue;
            if (!Cidr.IsValidIpv4(device.StaticIp))
            {
                logger.LogWarning("Skipping reservation for {Mac}: '{Ip}' is not a valid address", device.Mac, device.StaticIp);
                continue;
            }
            if (!Cidr.Contains(_options.LanNetwork, device.StaticIp))
            {
                logger.LogWarning("Skipping reservation for {Mac}: {Ip} is outside the LAN network {Network}",
                    device.Mac, device.StaticIp, _options.LanNetwork);
                continue;
            }

            var safeName = device.Mac.Replace(':', '-').ToLowerInvariant();
            await File.WriteAllTextAsync(
                Path.Combine(StaticLeaseDir, $"{safeName}.conf"),
                $"{device.Mac.ToLowerInvariant()},{device.StaticIp}\n", ct);
            count++;
        }

        logger.LogInformation("Wrote {Count} DHCP reservation(s)", count);
        await ReloadAsync(ct);
    }

    private async Task WriteConfigAsync(DhcpSettings settings, CancellationToken ct)
    {
        Directory.CreateDirectory(_options.DnsmasqConfigDirectory);
        Directory.CreateDirectory(StaticLeaseDir);

        var lines = new List<string>
        {
            "# Generated by PiRouter. Edits here are overwritten.",
            $"interface={_options.LanInterface}",
            "bind-interfaces",
            "domain-needed",
            "bogus-priv",
            $"dhcp-hostsdir={StaticLeaseDir}",
        };

        foreach (var server in _options.UpstreamDns)
            lines.Add($"server={server}");

        // Never inherit the host's /etc/resolv.conf. That file pointing at a resolver which
        // had stopped answering is exactly what took the tunnel down previously, and it
        // silently took every LAN client's DNS with it at the same time.
        lines.Add("no-resolv");

        if (settings.Enabled)
        {
            lines.Add($"dhcp-range={settings.RangeStart},{settings.RangeEnd},{settings.LeaseTime}");
            lines.Add($"dhcp-option=option:router,{_options.LanIp}");
            lines.Add($"dhcp-option=option:dns-server,{_options.LanIp}");
        }

        await File.WriteAllLinesAsync(ConfigFile, lines, ct);
        logger.LogInformation("Wrote dnsmasq config (DHCP {State}, upstreams {Servers})",
            settings.Enabled ? "enabled" : "disabled", string.Join(", ", _options.UpstreamDns));
    }

    /// <summary>SIGHUP makes dnsmasq re-read the hosts dir and flush its DNS cache.</summary>
    public async Task<bool> ReloadAsync(CancellationToken ct = default)
    {
        var pid = FindDnsmasqPid();
        if (pid is null)
        {
            logger.LogWarning("dnsmasq is not running, nothing to reload");
            return false;
        }

        var result = await runner.RunAsync(["kill", "-HUP", pid.Value.ToString()], allowFailure: true, ct: ct);
        if (result.Success) logger.LogInformation("Reloaded dnsmasq (pid {Pid})", pid);
        return result.Success;
    }

    public Task<bool> IsRunningAsync(CancellationToken ct = default) =>
        Task.FromResult(FindDnsmasqPid() is not null);

    /// <summary>
    /// Finds dnsmasq by scanning /proc. The container runs with pid:host, so this sees the
    /// real process wherever it lives — on the host or in the sibling dnsmasq container.
    /// </summary>
    private int? FindDnsmasqPid()
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories("/proc"))
            {
                var name = Path.GetFileName(directory);
                if (!int.TryParse(name, out var pid)) continue;

                try
                {
                    var comm = File.ReadAllText(Path.Combine(directory, "comm")).Trim();
                    if (comm == "dnsmasq") return pid;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Process exited between listing and reading, or is not ours to inspect.
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug("Could not scan /proc for dnsmasq: {Error}", ex.Message);
        }
        return null;
    }
}
