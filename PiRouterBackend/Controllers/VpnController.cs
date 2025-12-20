using Microsoft.AspNetCore.Mvc;
using PiRouterBackend.Services;

namespace PiRouterBackend.Controllers;

[ApiController]
[Route("api/vpn")]
public class VpnController : ControllerBase
{
    private readonly IVpnManager _vpnManager;
    private readonly ILogger<VpnController> _logger;

    public VpnController(IVpnManager vpnManager, ILogger<VpnController> logger)
    {
        _vpnManager = vpnManager;
        _logger = logger;
    }

    [HttpGet("profiles")]
    public async Task<IActionResult> ListProfiles()
    {
        try
        {
            var result = await _vpnManager.ListVpnConfigs();
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing VPN profiles");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("configs")]
    public async Task<IActionResult> ListConfigs()
    {
        // Legacy endpoint for backward compatibility
        return await ListProfiles();
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        try
        {
            var result = await _vpnManager.GetVpnStatus();
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting VPN status");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("connect/{profileName}")]
    public async Task<IActionResult> Connect(string profileName)
    {
        try
        {
            var result = await _vpnManager.ConnectVpn(profileName);
            if (result is IDictionary<string, object> dict && dict.ContainsKey("success") && dict["success"].ToString() == "False")
            {
                return BadRequest(result);
            }
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error connecting to VPN");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect()
    {
        try
        {
            var result = await _vpnManager.DisconnectVpn();
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disconnecting VPN");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("kill-switch")]
    public async Task<IActionResult> ToggleKillSwitch([FromQuery] bool enabled = false)
    {
        try
        {
            var result = await _vpnManager.ToggleKillSwitch(enabled);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error toggling kill switch");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("profile")]
    public async Task<IActionResult> AddProfile([FromQuery] string name, [FromQuery] string configContent)
    {
        try
        {
            var result = await _vpnManager.AddVpnProfile(name, configContent);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding VPN profile");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("profile/{name}")]
    public async Task<IActionResult> DeleteProfile(string name)
    {
        try
        {
            var result = await _vpnManager.DeleteVpnProfile(name);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting VPN profile");
            return BadRequest(new { error = ex.Message });
        }
    }
}
