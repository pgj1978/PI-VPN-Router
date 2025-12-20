using Microsoft.AspNetCore.Mvc;
using PiRouterBackend.Services;

namespace PiRouterBackend.Controllers;

[ApiController]
[Route("api/devices")]
public class DevicesController : ControllerBase
{
    private readonly IDeviceManager _deviceManager;
    private readonly ILogger<DevicesController> _logger;

    public DevicesController(IDeviceManager deviceManager, ILogger<DevicesController> logger)
    {
        _deviceManager = deviceManager;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> ListDevices()
    {
        try
        {
            var result = await _deviceManager.ListDevices();
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing devices");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{mac}/static-ip")]
    public async Task<IActionResult> SetStaticIp(string mac, [FromQuery] string? ip)
    {
        try
        {
            var result = await _deviceManager.SetStaticDeviceIp(mac, ip);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting static IP");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{mac}/bypass")]
    [HttpGet("{mac}/bypass")]
    public async Task<IActionResult> SetBypass(string mac, [FromQuery] bool bypass = false)
    {
        try
        {
            var result = await _deviceManager.SetDeviceBypass(mac, bypass);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting device bypass");
            return BadRequest(new { error = ex.Message });
        }
    }
}
