using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PiRouter.Core.Process;

public sealed record CommandResult(
    IReadOnlyList<string> Command,
    int ExitCode,
    string Stdout,
    string Stderr,
    TimeSpan Duration,
    bool TimedOut)
{
    public bool Success => ExitCode == 0 && !TimedOut;

    /// <summary>Stderr when the command failed, otherwise stdout. Trimmed.</summary>
    public string Output => (Success ? Stdout : string.IsNullOrWhiteSpace(Stderr) ? Stdout : Stderr).Trim();

    public string CommandLine => string.Join(' ', Command);
}

public interface IProcessRunner
{
    /// <param name="allowFailure">
    /// True for probes where a non-zero exit is an expected answer rather than a problem,
    /// so it is logged at debug instead of warning.
    /// </param>
    Task<CommandResult> RunAsync(
        IReadOnlyList<string> command,
        bool allowFailure = false,
        TimeSpan? timeout = null,
        CancellationToken ct = default);
}

/// <summary>
/// Runs external commands with a hard timeout and full structured logging of every
/// invocation. On a router the command log is the single most useful diagnostic, and
/// previously none of it was recorded anywhere a user could see.
/// </summary>
public sealed class ProcessRunner(ILogger<ProcessRunner> logger) : IProcessRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public async Task<CommandResult> RunAsync(
        IReadOnlyList<string> command,
        bool allowFailure = false,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Count == 0) throw new ArgumentException("Command cannot be empty", nameof(command));

        var startInfo = new ProcessStartInfo
        {
            FileName = command[0],
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        for (var i = 1; i < command.Count; i++) startInfo.ArgumentList.Add(command[i]);

        var stopwatch = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout ?? DefaultTimeout);

        try
        {
            using var process = System.Diagnostics.Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start: {command[0]}");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKill(process);
                stopwatch.Stop();
                var timedOut = new CommandResult(command, -1, string.Empty,
                    $"Timed out after {(timeout ?? DefaultTimeout).TotalSeconds:0}s", stopwatch.Elapsed, true);
                logger.LogWarning("Command timed out after {Elapsed}ms: {Command}",
                    stopwatch.ElapsedMilliseconds, timedOut.CommandLine);
                return timedOut;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            stopwatch.Stop();

            var result = new CommandResult(command, process.ExitCode, stdout, stderr, stopwatch.Elapsed, false);
            Log(result, allowFailure);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            var result = new CommandResult(command, -1, string.Empty, ex.Message, stopwatch.Elapsed, false);
            if (allowFailure)
                logger.LogDebug("Command could not run (tolerated): {Command} - {Error}", result.CommandLine, ex.Message);
            else
                logger.LogError(ex, "Command could not run: {Command}", result.CommandLine);
            return result;
        }
    }

    private void Log(CommandResult result, bool allowFailure)
    {
        if (result.Success)
        {
            logger.LogDebug("$ {Command} -> ok in {Elapsed}ms", result.CommandLine, result.Duration.TotalMilliseconds);
        }
        else if (allowFailure)
        {
            logger.LogDebug("$ {Command} -> exit {ExitCode} (tolerated): {Error}",
                result.CommandLine, result.ExitCode, result.Output);
        }
        else
        {
            logger.LogWarning("$ {Command} -> exit {ExitCode}: {Error}",
                result.CommandLine, result.ExitCode, result.Output);
        }
    }

    private void TryKill(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            logger.LogDebug("Could not kill timed-out process: {Error}", ex.Message);
        }
    }
}
