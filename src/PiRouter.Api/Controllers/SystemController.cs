using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PiRouter.Api.Contracts;
using PiRouter.Core.Configuration;
using PiRouter.Core.Process;
using PiRouter.Core.Services;

namespace PiRouter.Api.Controllers;

[ApiController]
[Route("api/system")]
[Produces("application/json")]
public sealed class SystemController(
    INetworkDiscovery discovery,
    IDnsmasqService dnsmasq,
    IReconciler reconciler,
    IProcessRunner processRunner,
    IOptions<RouterOptions> options,
    ILogger<SystemController> logger) : ControllerBase
{
    // Taken from the process itself. A static initialised on first use runs when this type is
    // first touched — i.e. on the first request — which reported an uptime of roughly zero.
    private static readonly DateTimeOffset StartedAt =
        new(System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero);

    private readonly RouterOptions _options = options.Value;

    /// <summary>The endpoint the old UI called and no controller implemented.</summary>
    [HttpGet("info")]
    public async Task<ActionResult<SystemInfoResponse>> GetInfo(CancellationToken ct) =>
        Ok(new SystemInfoResponse(
            LanInterface: _options.LanInterface,
            WanInterface: _options.WanInterface,
            VpnInterface: _options.VpnInterface,
            LanAddress: await discovery.GetInterfaceAddressAsync(_options.LanInterface, ct),
            WanAddress: await discovery.GetInterfaceAddressAsync(_options.WanInterface, ct),
            WanGateway: await discovery.GetDefaultGatewayAsync(_options.WanInterface, ct),
            IpForwarding: await discovery.IpForwardingEnabledAsync(ct),
            DnsmasqRunning: await dnsmasq.IsRunningAsync(ct),
            Version: typeof(SystemController).Assembly.GetName().Version?.ToString() ?? "unknown",
            Uptime: DateTimeOffset.UtcNow - StartedAt));

    [HttpGet("dhcp")]
    public async Task<ActionResult<DhcpResponse>> GetDhcp(CancellationToken ct)
    {
        var settings = await dnsmasq.GetSettingsAsync(ct);
        return Ok(new DhcpResponse(settings.Enabled, settings.RangeStart, settings.RangeEnd, settings.LeaseTime));
    }

    [HttpPut("dhcp")]
    public async Task<ActionResult<DhcpResponse>> SetDhcp([FromBody] SetDhcpRequest request, CancellationToken ct)
    {
        var current = await dnsmasq.GetSettingsAsync(ct);
        var settings = new DhcpSettings(
            request.Enabled,
            request.RangeStart ?? current.RangeStart,
            request.RangeEnd ?? current.RangeEnd,
            request.LeaseTime ?? current.LeaseTime);

        try
        {
            await dnsmasq.ApplySettingsAsync(settings, ct);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ErrorResponse(ex.Message));
        }

        return await GetDhcp(ct);
    }

    /// <summary>Compiled rules plus a diff against what is actually installed. Read-only.</summary>
    [HttpGet("rules")]
    public async Task<ActionResult<RulePreviewResponse>> GetRules(CancellationToken ct)
    {
        var desired = await reconciler.PreviewAsync(ct);
        var diff = await reconciler.DiffAsync(ct);

        return Ok(new RulePreviewResponse(
            desired.Fingerprint(),
            [.. desired.Describe()],
            diff.Missing,
            diff.Unexpected,
            diff.MissingChains,
            diff.InSync,
            reconciler.LastReconciledAt));
    }

    [HttpPost("reconcile")]
    public async Task<ActionResult<RulePreviewResponse>> Reconcile(CancellationToken ct)
    {
        await reconciler.ReconcileNowAsync(ct);
        return await GetRules(ct);
    }

    [HttpPost("reboot")]
    public async Task<IActionResult> Reboot(CancellationToken ct)
    {
        logger.LogWarning("Reboot requested via the API");

        // pid:host lets us signal init directly; nsenter enters the host mount namespace so
        // the host's own reboot binary is the one that runs.
        var result = await processRunner.RunAsync(
            ["nsenter", "-t", "1", "-m", "-u", "-n", "-i", "/sbin/reboot"],
            allowFailure: true, timeout: TimeSpan.FromSeconds(10), ct: ct);

        return result.Success
            ? Accepted(new { message = "Rebooting. The router will be back in about a minute." })
            : StatusCode(StatusCodes.Status500InternalServerError,
                new ErrorResponse($"Could not reboot: {result.Output}"));
    }
}
