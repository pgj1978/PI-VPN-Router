using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiRouter.Core.Configuration;
using PiRouter.Core.Firewall;

namespace PiRouter.Core.Services;

public interface IReconciler
{
    /// <summary>Ask for a reconcile as soon as possible. Cheap and safe to call repeatedly.</summary>
    void RequestReconcile(string reason);

    Task<RuleSet> ReconcileNowAsync(CancellationToken ct = default);
    Task<RuleSet> PreviewAsync(CancellationToken ct = default);
    Task<RuleDiff> DiffAsync(CancellationToken ct = default);
    DateTimeOffset? LastReconciledAt { get; }
    string? LastFingerprint { get; }
}

/// <summary>
/// Continuously drives the live system towards the compiled desired state.
///
/// This is what makes the router self-correcting. Previously, firewall state was mutated
/// only at the moment a user clicked something; anything that disturbed it afterwards — a
/// DHCP lease change, a VPN reconnect, netfilter-persistent restoring stale rules at boot —
/// left the system quietly wrong until somebody noticed and toggled the setting again.
/// </summary>
public sealed class ReconcilerService : BackgroundService, IReconciler
{
    private readonly IStateBuilder _stateBuilder;
    private readonly IRuleApplier _applier;
    private readonly IConfigStore _config;
    private readonly RouterOptions _options;
    private readonly ILogger<ReconcilerService> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wakeup = new(0);

    private string? _lastFingerprint;

    public ReconcilerService(
        IStateBuilder stateBuilder,
        IRuleApplier applier,
        IConfigStore config,
        IOptions<RouterOptions> options,
        ILogger<ReconcilerService> logger)
    {
        _stateBuilder = stateBuilder;
        _applier = applier;
        _config = config;
        _options = options.Value;
        _logger = logger;

        _config.Changed += () => RequestReconcile("configuration changed");
    }

    public DateTimeOffset? LastReconciledAt { get; private set; }
    public string? LastFingerprint => _lastFingerprint;

    public void RequestReconcile(string reason)
    {
        _logger.LogDebug("Reconcile requested: {Reason}", reason);
        if (_wakeup.CurrentCount == 0) _wakeup.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.ApplyRules)
        {
            _logger.LogWarning("ApplyRules is disabled - running in observe-only mode, no rules will be written");
            return;
        }

        // Force the first pass to write rules even if the system happens to look right,
        // so a restart always establishes known-good state.
        _lastFingerprint = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileNowAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A reconcile must never kill the loop; the next tick may well succeed.
                _logger.LogError(ex, "Reconcile failed; will retry");
            }

            try
            {
                using var delay = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                delay.CancelAfter(TimeSpan.FromSeconds(_options.ReconcileIntervalSeconds));
                await _wakeup.WaitAsync(delay.Token);
            }
            catch (OperationCanceledException)
            {
                // Timer elapsed rather than an explicit wake-up. Either way, loop round.
            }
        }
    }

    public async Task<RuleSet> ReconcileNowAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var desired = RuleCompiler.Compile(await _stateBuilder.BuildAsync(ct));
            var fingerprint = desired.Fingerprint();

            if (fingerprint == _lastFingerprint)
            {
                // Desired state is unchanged, but the live system may still have drifted
                // underneath us — that is exactly the failure this loop exists to catch.
                var diff = await _applier.DiffAsync(desired, ct);
                if (diff.InSync)
                {
                    LastReconciledAt = DateTimeOffset.UtcNow;
                    return desired;
                }

                _logger.LogWarning("Firewall drift detected ({Missing} missing, {Unexpected} unexpected, {Chains} missing chains); repairing",
                    diff.Missing.Count, diff.Unexpected.Count, diff.MissingChains.Count);
                foreach (var rule in diff.Missing.Take(10)) _logger.LogWarning("  missing: {Rule}", rule);
                foreach (var rule in diff.Unexpected.Take(10)) _logger.LogWarning("  unexpected: {Rule}", rule);
            }
            else if (_lastFingerprint is not null)
            {
                _logger.LogInformation("Desired state changed {Old} -> {New}", _lastFingerprint, fingerprint);
            }

            await _applier.ApplyAsync(desired, ct);
            _lastFingerprint = fingerprint;
            LastReconciledAt = DateTimeOffset.UtcNow;
            return desired;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Compiles the ruleset without touching the system.</summary>
    public async Task<RuleSet> PreviewAsync(CancellationToken ct = default) =>
        RuleCompiler.Compile(await _stateBuilder.BuildAsync(ct));

    public async Task<RuleDiff> DiffAsync(CancellationToken ct = default) =>
        await _applier.DiffAsync(await PreviewAsync(ct), ct);

    public override void Dispose()
    {
        _gate.Dispose();
        _wakeup.Dispose();
        base.Dispose();
    }
}
