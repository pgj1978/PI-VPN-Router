using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace PiRouter.Core.Services;

public interface IDomainResolver
{
    Task<IReadOnlyList<string>> ResolveAsync(string domain, CancellationToken ct = default);
    IReadOnlyList<string> LastKnown(string domain);
}

/// <summary>
/// Resolves bypass domains, keeping the last successful answer.
///
/// Two behaviours the old implementation lacked. It resolved once when the domain was added
/// and never again, so rules silently pointed at addresses the service had moved off; and a
/// single failed lookup produced an empty list, which would have torn down working rules.
/// Here a failure falls back to the last known good answer instead.
/// </summary>
public sealed class DomainResolver(ILogger<DomainResolver> logger) : IDomainResolver
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<string>> ResolveAsync(string domain, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(domain)) return [];

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(domain, AddressFamily.InterNetwork, ct);
            var ips = addresses
                .Select(a => a.ToString())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(a => a, StringComparer.Ordinal)
                .ToList();

            if (ips.Count == 0)
            {
                logger.LogWarning("{Domain} resolved to no IPv4 addresses", domain);
                return LastKnown(domain);
            }

            if (_cache.TryGetValue(domain, out var previous) && !previous.SequenceEqual(ips))
                logger.LogInformation("{Domain} changed address: [{Old}] -> [{New}]",
                    domain, string.Join(", ", previous), string.Join(", ", ips));

            _cache[domain] = ips;
            return ips;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            var fallback = LastKnown(domain);
            logger.LogWarning("Could not resolve {Domain} ({Error}); keeping {Count} cached address(es)",
                domain, ex.Message, fallback.Count);
            return fallback;
        }
    }

    public IReadOnlyList<string> LastKnown(string domain) =>
        _cache.TryGetValue(domain, out var ips) ? ips : [];
}
