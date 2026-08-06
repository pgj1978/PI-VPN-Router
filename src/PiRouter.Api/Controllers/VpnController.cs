using Microsoft.AspNetCore.Mvc;
using PiRouter.Api.Contracts;
using PiRouter.Core.Services;

namespace PiRouter.Api.Controllers;

[ApiController]
[Route("api/vpn")]
[Produces("application/json")]
public sealed class VpnController(
    IVpnService vpn,
    IConfigStore config,
    IReconciler reconciler,
    ILogger<VpnController> logger) : ControllerBase
{
    [HttpGet("status")]
    public async Task<ActionResult<VpnStatusResponse>> GetStatus(CancellationToken ct)
    {
        var status = await vpn.GetStatusAsync(ct);
        var peer = status.PrimaryPeer;

        return Ok(new VpnStatusResponse(
            Connected: status.Up,
            Profile: config.Current.ActiveVpnProfile,
            InterfaceName: status.InterfaceName,
            Endpoint: peer?.Endpoint,
            HandshakeAgeSeconds: peer?.HandshakeAge?.TotalSeconds,
            BytesReceived: status.TotalReceived,
            BytesSent: status.TotalSent,
            Healthy: status.IsHealthy(TimeSpan.FromMinutes(3))));
    }

    [HttpGet("profiles")]
    public async Task<ActionResult<VpnProfilesResponse>> ListProfiles(CancellationToken ct)
    {
        var profiles = await vpn.ListProfilesAsync(ct);

        return Ok(new VpnProfilesResponse(
            [.. profiles.Select(p => new VpnProfileResponse(p.Name, p.Endpoint, p.Dns, p.Mtu, p.Active))],
            config.Current.KillSwitchEnabled,
            config.Current.ActiveVpnProfile));
    }

    [HttpPost("connect/{name}")]
    public async Task<ActionResult<VpnOperationResponse>> Connect(string name, CancellationToken ct)
    {
        if (!VpnProfileName.IsValid(name))
            return BadRequest(new ErrorResponse($"Invalid profile name: {name}"));

        var result = await vpn.ConnectAsync(name, ct);

        if (result.Success)
        {
            // Record intent before reconciling so the watchdog knows which profile to keep alive.
            await config.MutateAsync(c => c.ActiveVpnProfile = name, ct);
            await reconciler.ReconcileNowAsync(ct);
            logger.LogInformation("Connected to {Profile}", name);
        }

        var response = new VpnOperationResponse(result.Success, result.Error, result.Log);
        return result.Success ? Ok(response) : StatusCode(StatusCodes.Status502BadGateway, response);
    }

    [HttpPost("disconnect")]
    public async Task<ActionResult<VpnOperationResponse>> Disconnect(CancellationToken ct)
    {
        var result = await vpn.DisconnectAsync(ct);

        // Clearing the active profile stops the watchdog immediately reconnecting what the
        // user just asked to disconnect.
        await config.MutateAsync(c => c.ActiveVpnProfile = null, ct);
        await reconciler.ReconcileNowAsync(ct);

        return Ok(new VpnOperationResponse(result.Success, result.Error, result.Log));
    }

    [HttpPost("kill-switch")]
    public async Task<ActionResult<VpnProfilesResponse>> SetKillSwitch(
        [FromBody] KillSwitchRequest request, CancellationToken ct)
    {
        await config.MutateAsync(c => c.KillSwitchEnabled = request.Enabled, ct);

        // Apply straight away rather than waiting for the next tick: a kill switch that
        // takes effect "eventually" is not a kill switch.
        await reconciler.ReconcileNowAsync(ct);
        logger.LogWarning("Kill switch {State}", request.Enabled ? "ENABLED" : "disabled");

        return await ListProfiles(ct);
    }

    [HttpPost("profiles")]
    public async Task<ActionResult<VpnProfileResponse>> AddProfile(
        [FromBody] AddVpnProfileRequest request, CancellationToken ct)
    {
        if (!VpnProfileName.IsValid(request.Name))
            return BadRequest(new ErrorResponse(
                "Profile names may contain letters, digits, dots, dashes and underscores only"));

        if (!request.Config.Contains("[Interface]", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new ErrorResponse("That does not look like a WireGuard configuration"));

        await vpn.SaveProfileAsync(request.Name, request.Config, ct);

        var profile = (await vpn.ListProfilesAsync(ct)).FirstOrDefault(p => p.Name == request.Name);
        return profile is null
            ? StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse("Profile was saved but could not be read back"))
            : Ok(new VpnProfileResponse(profile.Name, profile.Endpoint, profile.Dns, profile.Mtu, profile.Active));
    }

    [HttpDelete("profiles/{name}")]
    public async Task<IActionResult> DeleteProfile(string name, CancellationToken ct)
    {
        if (!VpnProfileName.IsValid(name))
            return BadRequest(new ErrorResponse($"Invalid profile name: {name}"));

        if (string.Equals(config.Current.ActiveVpnProfile, name, StringComparison.Ordinal))
            return Conflict(new ErrorResponse("Disconnect this profile before deleting it"));

        await vpn.DeleteProfileAsync(name, ct);
        return NoContent();
    }
}
