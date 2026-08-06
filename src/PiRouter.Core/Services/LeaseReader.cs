using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiRouter.Core.Configuration;
using PiRouter.Core.Net;

namespace PiRouter.Core.Services;

public sealed record DhcpLease(string Mac, string Ip, string? Hostname, DateTimeOffset? Expires);

public interface ILeaseReader
{
    Task<IReadOnlyList<DhcpLease>> ReadAsync(CancellationToken ct = default);
    Task<string?> ResolveMacAsync(string mac, CancellationToken ct = default);
}

/// <summary>
/// Reads dnsmasq's lease file, which is the authority on which address a MAC currently holds.
///
/// Resolving MAC to IP on demand — rather than trusting an IP written into the config months
/// ago — is what stops bypass rules pointing at addresses the device no longer has. The old
/// config still contains a device pinned to 192.168.6.186, an address from a subnet that no
/// longer exists on this network.
/// </summary>
public sealed class LeaseReader(
    IOptions<RouterOptions> options,
    ILogger<LeaseReader> logger) : ILeaseReader
{
    private readonly string _leaseFile = options.Value.DnsmasqLeaseFile;

    public async Task<IReadOnlyList<DhcpLease>> ReadAsync(CancellationToken ct = default)
    {
        var leases = new List<DhcpLease>();
        try
        {
            if (!File.Exists(_leaseFile))
            {
                logger.LogDebug("Lease file {Path} does not exist yet", _leaseFile);
                return leases;
            }

            foreach (var line in await File.ReadAllLinesAsync(_leaseFile, ct))
            {
                // Format: <expiry unix> <mac> <ip> <hostname> <client-id>
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;
                if (!Cidr.IsValidIpv4(parts[2])) continue;

                var expiry = long.TryParse(parts[0], CultureInfo.InvariantCulture, out var unix) && unix > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(unix)
                    : (DateTimeOffset?)null;

                var hostname = parts.Length > 3 && parts[3] != "*" ? parts[3] : null;

                leases.Add(new DhcpLease(parts[1].ToLowerInvariant(), parts[2], hostname, expiry));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read leases from {Path}", _leaseFile);
        }
        return leases;
    }

    public async Task<string?> ResolveMacAsync(string mac, CancellationToken ct = default)
    {
        var normalised = mac.Trim().ToLowerInvariant();
        var leases = await ReadAsync(ct);
        return leases.FirstOrDefault(l => l.Mac == normalised)?.Ip;
    }
}
