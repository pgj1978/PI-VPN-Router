using Microsoft.AspNetCore.Mvc;
using PiRouterBackend.Services;

namespace PiRouterBackend.Controllers;

[ApiController]
[Route("api/domains")]
public class DomainsController : ControllerBase
{
    private readonly IDomainManager _domainManager;
    private readonly ILogger<DomainsController> _logger;

    public DomainsController(IDomainManager domainManager, ILogger<DomainsController> logger)
    {
        _domainManager = domainManager;
        _logger = logger;
    }

    [HttpGet("bypass")]
    public async Task<IActionResult> ListBypass()
    {
        try
        {
            var result = await _domainManager.ListDomainBypasses();
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing domain bypasses");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("bypass")]
    public async Task<IActionResult> AddBypass([FromQuery] string domain)
    {
        try
        {
            var result = await _domainManager.AddDomainBypass(domain);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding domain bypass");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("bypass")]
    public async Task<IActionResult> RemoveBypass([FromQuery] string domain)
    {
        try
        {
            var result = await _domainManager.RemoveDomainBypass(domain);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing domain bypass");
            return BadRequest(new { error = ex.Message });
        }
    }
}
