using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace PiRouter.Core.Logging;

public sealed record LogEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    string Level,
    string Category,
    string Message,
    string? Exception);

/// <summary>
/// A bounded in-memory ring of recent log entries, plus a live feed for streaming clients.
///
/// The router's logs previously only existed inside `docker logs`, which meant diagnosing
/// anything required SSH. Keeping the recent history in process is what lets the UI show it.
/// </summary>
public sealed class LogBuffer(int capacity = 5000)
{
    private readonly LinkedList<LogEntry> _entries = new();
    private readonly Lock _lock = new();
    private readonly List<Channel<LogEntry>> _subscribers = [];
    private long _sequence;

    public int Capacity { get; } = capacity;

    public void Add(DateTimeOffset timestamp, string level, string category, string message, string? exception)
    {
        LogEntry entry;
        lock (_lock)
        {
            entry = new LogEntry(++_sequence, timestamp, level, category, message, exception);
            _entries.AddLast(entry);
            while (_entries.Count > Capacity) _entries.RemoveFirst();
        }

        // Publish outside the lock. A subscriber that cannot keep up drops entries rather
        // than blocking the logging path — a slow browser must never stall the router.
        List<Channel<LogEntry>> subscribers;
        lock (_lock) subscribers = [.. _subscribers];
        foreach (var channel in subscribers) channel.Writer.TryWrite(entry);
    }

    public IReadOnlyList<LogEntry> Snapshot(
        string? minLevel = null, string? search = null, long? afterSequence = null, int limit = 500)
    {
        List<LogEntry> entries;
        lock (_lock) entries = [.. _entries];

        IEnumerable<LogEntry> query = entries;

        if (afterSequence is { } after)
            query = query.Where(e => e.Sequence > after);

        if (!string.IsNullOrWhiteSpace(minLevel) && LevelRank(minLevel) is { } threshold)
            query = query.Where(e => LevelRank(e.Level) >= threshold);

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(e =>
                e.Message.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                e.Category.Contains(search, StringComparison.OrdinalIgnoreCase));

        return [.. query.TakeLast(Math.Clamp(limit, 1, Capacity))];
    }

    public (IDisposable Subscription, ChannelReader<LogEntry> Reader) Subscribe()
    {
        var channel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        lock (_lock) _subscribers.Add(channel);

        return (new Subscription(this, channel), channel.Reader);
    }

    private void Unsubscribe(Channel<LogEntry> channel)
    {
        lock (_lock) _subscribers.Remove(channel);
        channel.Writer.TryComplete();
    }

    private static int? LevelRank(string level) => level.ToLowerInvariant() switch
    {
        "trace" => 0,
        "debug" => 1,
        "information" or "info" => 2,
        "warning" or "warn" => 3,
        "error" => 4,
        "critical" => 5,
        _ => null,
    };

    private sealed class Subscription(LogBuffer buffer, Channel<LogEntry> channel) : IDisposable
    {
        public void Dispose() => buffer.Unsubscribe(channel);
    }
}

[ProviderAlias("Buffer")]
public sealed class LogBufferProvider(LogBuffer buffer) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new BufferLogger(buffer, categoryName);
    public void Dispose() { }

    private sealed class BufferLogger(LogBuffer buffer, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            buffer.Add(
                DateTimeOffset.UtcNow,
                logLevel.ToString(),
                ShortCategory(category),
                formatter(state, exception),
                exception?.ToString());
        }

        /// <summary>"PiRouter.Core.Services.VpnService" -> "VpnService".</summary>
        private static string ShortCategory(string category)
        {
            var lastDot = category.LastIndexOf('.');
            return lastDot >= 0 && lastDot < category.Length - 1 ? category[(lastDot + 1)..] : category;
        }
    }
}
