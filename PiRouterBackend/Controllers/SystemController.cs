using Microsoft.AspNetCore.Mvc;
using PiRouterBackend.Services;

namespace PiRouterBackend.Controllers;

[ApiController]
[Route("api/system")]
public class SystemController : ControllerBase
{
    private readonly ISystemManager _systemManager;
    private readonly ILogger<SystemController> _logger;

    public SystemController(ISystemManager systemManager, ILogger<SystemController> logger)
    {
        _systemManager = systemManager;
        _logger = logger;
    }

    [HttpGet("dhcp")]
    public async Task<IActionResult> GetDhcpStatus()
    {
        try
        {
            var result = await _systemManager.GetDhcpStatus();
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting DHCP status");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("dhcp")]
    public async Task<IActionResult> SetDhcpStatus([FromQuery] bool enable, [FromQuery] string? startIp, [FromQuery] string? endIp, [FromQuery] string? leaseTime)
    {
        try
        {
            var result = await _systemManager.SetDhcpStatus(enable, startIp, endIp, leaseTime);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting DHCP status");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("eth1-ip")]
    public async Task<IActionResult> GetEth1IpConfig()
    {
        try
        {
            var result = await _systemManager.GetEth1IpConfig();
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting eth1 IP config");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("eth1-ip")]
    public async Task<IActionResult> SetEth1IpConfig([FromQuery] string ipAddress, [FromQuery] string subnetMask)
    {
        try
        {
            var result = await _systemManager.SetEth1IpConfig(ipAddress, subnetMask);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting eth1 IP config");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("reboot")]
    public async Task<IActionResult> Reboot()
    {
        try
        {
            await _systemManager.RebootSystem();
            return Ok(new { message = "System is rebooting..." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rebooting system");
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
