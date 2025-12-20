namespace PiRouterBackend.Services;

public interface IProcessRunner
{
    Task<(bool Success, string Output)> RunCommandAsync(string[] cmd, bool useSudo = false, bool logFailure = true);
}

public class ProcessRunner : IProcessRunner
{
    private readonly ILogger<ProcessRunner> _logger;

    public ProcessRunner(ILogger<ProcessRunner> logger)
    {
        _logger = logger;
    }

    public async Task<(bool Success, string Output)> RunCommandAsync(string[] cmd, bool useSudo = false, bool logFailure = true)
    {
        try
        {
            // In Docker container with privileged mode, we don't need sudo
            var processCmd = cmd;

            var processInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = processCmd[0],
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            for (int i = 1; i < processCmd.Length; i++)
            {
                processInfo.ArgumentList.Add(processCmd[i]);
            }

            using (var process = System.Diagnostics.Process.Start(processInfo))
            {
                if (process == null)
                    return (false, "Failed to start process");

                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();

                await Task.Run(() => process.WaitForExit());

                if (process.ExitCode != 0)
                {
                    if (logFailure)
                    {
                        _logger.LogError("Command failed: {Command}, Error: {Error}", string.Join(" ", processCmd), error);
                    }
                    return (false, error);
                }

                return (true, output);
            }
        }
        catch (Exception ex)
        {
            if (logFailure)
            {
                _logger.LogError(ex, "Exception running command: {Command}", string.Join(" ", cmd));
            }
            return (false, ex.Message);
        }
    }
}
