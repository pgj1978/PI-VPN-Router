using Microsoft.AspNetCore.Mvc;
using PiRouter.Api.Contracts;
using PiRouter.Core.Services;

namespace PiRouter.Api.Controllers;

[ApiController]
[Route("api/diagnostics")]
[Produces("application/json")]
public sealed class DiagnosticsController(IDiagnosticsService diagnostics) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<DiagnosticsResponse>> Run(CancellationToken ct)
    {
        var report = await diagnostics.RunAsync(ct);

        return Ok(new DiagnosticsResponse(
            report.RanAt,
            report.Overall.ToString().ToLowerInvariant(),
            [.. report.Checks.Select(c => new DiagnosticCheckResponse(
                c.Id, c.Name, c.Status.ToString().ToLowerInvariant(), c.Detail, c.Remediation))]));
    }
}
