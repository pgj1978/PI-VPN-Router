using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using PiRouter.Api.Contracts;
using PiRouter.Core.Models;
using PiRouter.Core.Services;

namespace PiRouter.Api.Controllers;

[ApiController]
[Route("api/domains")]
[Produces("application/json")]
public sealed partial class DomainsController(
    IConfigStore config,
    IDomainResolver resolver,
    IReconciler reconciler,
    ILogger<DomainsController> logger) : ControllerBase
{
    [HttpGet]
    public ActionResult<DomainsResponse> List() =>
        Ok(new DomainsResponse(
            [.. config.Current.DomainBypasses.Select(d => new DomainResponse(
                d.Domain, d.Enabled, resolver.LastKnown(d.Domain), d.LastResolvedAt))]));

    [HttpPost]
    public async Task<ActionResult<DomainsResponse>> Add([FromBody] AddDomainRequest request, CancellationToken ct)
    {
        var domain = request.Domain.Trim().TrimEnd('.').ToLowerInvariant();

        if (!HostnamePattern().IsMatch(domain))
            return BadRequest(new ErrorResponse($"'{request.Domain}' is not a valid hostname"));

        if (config.Current.DomainBypasses.Any(d => d.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase)))
            return Conflict(new ErrorResponse($"{domain} is already in the bypass list"));

        // Resolve before saving so the user finds out immediately that a name is wrong,
        // rather than silently adding an entry that can never produce any rules.
        var ips = await resolver.ResolveAsync(domain, ct);
        if (ips.Count == 0)
            return BadRequest(new ErrorResponse($"{domain} could not be resolved, so no bypass rules could be built for it"));

        await config.MutateAsync(c => c.DomainBypasses.Add(new DomainBypassConfig
        {
            Domain = domain,
            Enabled = true,
            LastResolvedIps = [.. ips],
            LastResolvedAt = DateTimeOffset.UtcNow,
        }), ct);

        await reconciler.ReconcileNowAsync(ct);
        logger.LogInformation("Added domain bypass {Domain} -> [{Ips}]", domain, string.Join(", ", ips));

        return List();
    }

    [HttpDelete("{domain}")]
    public async Task<ActionResult<DomainsResponse>> Remove(string domain, CancellationToken ct)
    {
        var normalised = Uri.UnescapeDataString(domain).Trim().ToLowerInvariant();

        var removed = 0;
        await config.MutateAsync(c => removed = c.DomainBypasses.RemoveAll(d =>
            d.Domain.Equals(normalised, StringComparison.OrdinalIgnoreCase)), ct);

        if (removed == 0) return NotFound(new ErrorResponse($"{normalised} is not in the bypass list"));

        await reconciler.ReconcileNowAsync(ct);
        logger.LogInformation("Removed domain bypass {Domain}", normalised);

        return List();
    }

    /// <summary>Forces re-resolution now instead of waiting for the next scheduled refresh.</summary>
    [HttpPost("refresh")]
    public async Task<ActionResult<DomainsResponse>> Refresh(CancellationToken ct)
    {
        foreach (var entry in config.Current.DomainBypasses.Where(d => d.Enabled))
        {
            var ips = await resolver.ResolveAsync(entry.Domain, ct);
            await config.MutateAsync(c =>
            {
                var target = c.DomainBypasses.FirstOrDefault(d =>
                    d.Domain.Equals(entry.Domain, StringComparison.OrdinalIgnoreCase));
                if (target is null) return;
                target.LastResolvedIps = [.. ips];
                target.LastResolvedAt = DateTimeOffset.UtcNow;
            }, ct);
        }

        await reconciler.ReconcileNowAsync(ct);
        return List();
    }

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,}$")]
    private static partial Regex HostnamePattern();
}
