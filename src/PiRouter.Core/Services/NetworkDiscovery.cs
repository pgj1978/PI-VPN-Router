using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiRouter.Core.Configuration;
using PiRouter.Core.Process;

namespace PiRouter.Core.Services;

public interface INetworkDiscovery
{
    Task<string?> GetInterfaceAddressAsync(string iface, CancellationToken ct = default);
    Task<string?> GetDefaultGatewayAsync(string iface, CancellationToken ct = default);
    Task<bool> InterfaceExistsAsync(string iface, CancellationToken ct = default);
    Task<bool> IpForwardingEnabledAsync(CancellationToken ct = default);
}

/// <summary>
/// Reads live network facts instead of assuming them. Every value the old code hardcoded —
/// the LAN address, the upstream gateway — is discovered here, so moving the router to a
/// different network needs no code change.
/// </summary>
public sealed partial class NetworkDiscovery(
    IProcessRunner runner,
    IOptions<RouterOptions> options,
    ILogger<NetworkDiscovery> logger) : INetworkDiscovery
{
    private readonly RouterOptions _options = options.Value;

    /// <summary>Returns the interface address with prefix, e.g. "192.168.20.1/24".</summary>
    public async Task<string?> GetInterfaceAddressAsync(string iface, CancellationToken ct = default)
    {
        var result = await runner.RunAsync(["ip", "-4", "-o", "addr", "show", "dev", iface], allowFailure: true, ct: ct);
        if (!result.Success) return null;

        var match = InetPattern().Match(result.Stdout);
        return match.Success ? match.Groups["cidr"].Value : null;
    }

    public async Task<string?> GetDefaultGatewayAsync(string iface, CancellationToken ct = default)
    {
        // Ask the kernel which gateway it would actually use for this interface rather than
        // trusting a constant. The previous code hardcoded 192.168.5.1.
        var result = await runner.RunAsync(["ip", "-4", "route", "show", "default", "dev", iface],
            allowFailure: true, ct: ct);

        if (result.Success)
        {
            var match = ViaPattern().Match(result.Stdout);
            if (match.Success) return match.Groups["gw"].Value;
        }

        // Fall back to the global default route, which is correct whenever the WAN is the
        // only uplink but the route is not pinned to the interface.
        var global = await runner.RunAsync(["ip", "-4", "route", "show", "default"], allowFailure: true, ct: ct);
        if (global.Success)
        {
            foreach (var line in global.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.Contains($"dev {iface}", StringComparison.Ordinal)) continue;
                var match = ViaPattern().Match(line);
                if (match.Success) return match.Groups["gw"].Value;
            }
        }

        logger.LogWarning("Could not determine the upstream gateway for {Interface}; bypass routing will be unavailable", iface);
        return _options.WanGateway;
    }

    public async Task<bool> InterfaceExistsAsync(string iface, CancellationToken ct = default) =>
        (await runner.RunAsync(["ip", "link", "show", "dev", iface], allowFailure: true, ct: ct)).Success;

    public async Task<bool> IpForwardingEnabledAsync(CancellationToken ct = default)
    {
        try
        {
            const string path = "/proc/sys/net/ipv4/ip_forward";
            return File.Exists(path) && (await File.ReadAllTextAsync(path, ct)).Trim() == "1";
        }
        catch (Exception ex)
        {
            logger.LogDebug("Could not read ip_forward: {Error}", ex.Message);
            return false;
        }
    }

    [GeneratedRegex(@"inet\s+(?<cidr>\d{1,3}(?:\.\d{1,3}){3}/\d{1,2})")]
    private static partial Regex InetPattern();

    [GeneratedRegex(@"via\s+(?<gw>\d{1,3}(?:\.\d{1,3}){3})")]
    private static partial Regex ViaPattern();
}
