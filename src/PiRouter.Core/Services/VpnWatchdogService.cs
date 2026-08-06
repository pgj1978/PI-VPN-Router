using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiRouter.Core.Configuration;

namespace PiRouter.Core.Services;

/// <summary>
/// Keeps the configured tunnel up.
///
/// On startup it restores the profile the user last selected, and thereafter it watches the
/// handshake age and reconnects when the tunnel has gone silent. Backoff is exponential so
/// that a genuinely unreachable endpoint — no internet, DNS broken — does not produce a
/// reconnect attempt every fifteen seconds forever.
/// </summary>
public sealed class VpnWatchdogService(
    IVpnService vpn,
    IConfigStore config,
    IReconciler reconciler,
    IOptions<RouterOptions> options,
    ILogger<VpnWatchdogService> logger) : BackgroundService
{
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
    ];

    private readonly RouterOptions _options = options.Value;
    private int _consecutiveFailures;
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;

    /// <summary>Suppresses reconnects while a user-initiated connect or disconnect is in flight.</summary>
    public bool Paused { get; set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the rest of the stack a moment to come up before touching the network.
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "VPN watchdog check failed");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        if (Paused) return;

        var wanted = config.Current.ActiveVpnProfile;
        if (string.IsNullOrWhiteSpace(wanted)) return;   // user has not asked for a tunnel

        var status = await vpn.GetStatusAsync(ct);
        var maxAge = TimeSpan.FromSeconds(_options.VpnStaleHandshakeSeconds);

        if (status.IsHealthy(maxAge))
        {
            if (_consecutiveFailures > 0)
                logger.LogInformation("Tunnel recovered after {Failures} failed attempt(s)", _consecutiveFailures);
            _consecutiveFailures = 0;
            return;
        }

        // A tunnel that is up but has never handshaked is still starting; give it a grace period.
        if (status.Up && status.PrimaryPeer?.LatestHandshake is null && _consecutiveFailures == 0)
        {
            logger.LogDebug("Tunnel is up but has not handshaken yet; waiting");
            _consecutiveFailures++;
            return;
        }

        if (DateTimeOffset.UtcNow < _nextAttempt) return;

        var reason = status.Up
            ? $"handshake is stale ({status.PrimaryPeer?.HandshakeAge?.TotalSeconds:0}s old)"
            : "the interface is down";

        logger.LogWarning("Reconnecting VPN profile {Profile}: {Reason}", wanted, reason);

        var result = await vpn.ConnectAsync(wanted, ct);
        if (result.Success)
        {
            _consecutiveFailures = 0;
            _nextAttempt = DateTimeOffset.MinValue;
            reconciler.RequestReconcile("VPN reconnected by watchdog");
        }
        else
        {
            var delay = Backoff[Math.Min(_consecutiveFailures, Backoff.Length - 1)];
            _consecutiveFailures++;
            _nextAttempt = DateTimeOffset.UtcNow + delay;
            logger.LogError("Reconnect failed ({Error}); next attempt in {Delay}", result.Error, delay);
        }
    }
}
