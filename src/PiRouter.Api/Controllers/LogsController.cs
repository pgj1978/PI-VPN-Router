using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using PiRouter.Api.Contracts;
using PiRouter.Core.Logging;

namespace PiRouter.Api.Controllers;

[ApiController]
[Route("api/logs")]
public sealed class LogsController(LogBuffer buffer) : ControllerBase
{
    private static readonly JsonSerializerOptions StreamJson = new(JsonSerializerDefaults.Web);

    [HttpGet]
    [Produces("application/json")]
    public ActionResult<LogsResponse> Get(
        [FromQuery] string? level,
        [FromQuery] string? search,
        [FromQuery] long? after,
        [FromQuery] int limit = 500) =>
        Ok(new LogsResponse([.. buffer.Snapshot(level, search, after, limit).Select(Map)]));

    /// <summary>
    /// Server-sent events feed of new entries. SSE rather than websockets: it is one-way,
    /// reconnects on its own, and needs no protocol upgrade through nginx.
    /// </summary>
    [HttpGet("stream")]
    public async Task Stream([FromQuery] string? level, [FromQuery] string? search, CancellationToken ct)
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no"; // stop nginx buffering the stream

        var (subscription, reader) = buffer.Subscribe();
        using (subscription)
        {
            // Seed with recent history so the page is useful the instant it opens.
            foreach (var entry in buffer.Snapshot(level, search, limit: 200))
                await WriteAsync(entry, ct);

            try
            {
                while (await reader.WaitToReadAsync(ct))
                {
                    while (reader.TryRead(out var entry))
                    {
                        if (!Matches(entry, level, search)) continue;
                        await WriteAsync(entry, ct);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Client navigated away or disconnected. Normal.
            }
        }
    }

    private async Task WriteAsync(LogEntry entry, CancellationToken ct)
    {
        await Response.WriteAsync($"data: {JsonSerializer.Serialize(Map(entry), StreamJson)}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }

    private static bool Matches(LogEntry entry, string? level, string? search)
    {
        if (!string.IsNullOrWhiteSpace(search)
            && !entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
            && !entry.Category.Contains(search, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(level)) return true;
        return Rank(entry.Level) >= Rank(level);
    }

    private static int Rank(string level) => level.ToLowerInvariant() switch
    {
        "trace" => 0,
        "debug" => 1,
        "information" or "info" => 2,
        "warning" or "warn" => 3,
        "error" => 4,
        "critical" => 5,
        _ => 2,
    };

    private static LogEntryResponse Map(LogEntry e) =>
        new(e.Sequence, e.Timestamp, e.Level, e.Category, e.Message, e.Exception);
}
