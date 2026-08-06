using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PiRouter.Core.Process;

namespace PiRouter.Core.Firewall;

public sealed record RuleDiff(
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Unexpected,
    IReadOnlyList<string> MissingChains)
{
    public bool InSync => Missing.Count == 0 && Unexpected.Count == 0 && MissingChains.Count == 0;
}

public interface IRuleApplier
{
    Task ApplyAsync(RuleSet desired, CancellationToken ct = default);
    Task<RuleDiff> DiffAsync(RuleSet desired, CancellationToken ct = default);
    Task RemoveAllAsync(CancellationToken ct = default);
}

/// <summary>
/// Applies a compiled <see cref="RuleSet"/> to the live system.
///
/// The contract that makes this safe: PiRouter creates four chains of its own and only ever
/// writes inside them. Built-in chains receive a single jump rule each and are otherwise
/// untouched, so Docker's rules are never disturbed. INPUT is never touched at all — the
/// machine administering this router reaches it over the LAN, and filtering INPUT would
/// risk locking the operator out.
///
/// Every apply flushes our chains and rebuilds them from scratch. There is no incremental
/// add/delete path, which is precisely why rules can no longer accumulate or go stale: the
/// two historical bypass bugs were both a failed or missed targeted delete.
/// </summary>
public sealed class RuleApplier(IProcessRunner runner, ILogger<RuleApplier> logger) : IRuleApplier
{
    private static readonly Regex IpRulePattern = new(@"^(?<prio>\d+):\s+(?<body>.+)$", RegexOptions.Compiled);

    public async Task ApplyAsync(RuleSet desired, CancellationToken ct = default)
    {
        await EnsureChainsAsync(ct);
        await ApplyFirewallAsync(desired, ct);
        await ApplyIpRulesAsync(desired, ct);
        await ApplyIpRoutesAsync(desired, ct);
        await runner.RunAsync(["ip", "route", "flush", "cache"], allowFailure: true, ct: ct);

        logger.LogInformation("Applied ruleset {Fingerprint}: {FirewallCount} firewall rules, {RuleCount} ip rules, {RouteCount} routes",
            desired.Fingerprint(), desired.Firewall.Count, desired.IpRules.Count, desired.IpRoutes.Count);
    }

    /// <summary>Creates our chains if absent and makes sure each built-in chain jumps to them exactly once.</summary>
    private async Task EnsureChainsAsync(CancellationToken ct)
    {
        foreach (var binding in Chains.All)
        {
            // -N fails when the chain already exists, which is the normal steady state.
            await runner.RunAsync(["iptables", "-t", binding.Table, "-N", binding.Name], allowFailure: true, ct: ct);

            // -C tests for the jump without adding it, so this stays idempotent. Appending
            // rather than inserting leaves Docker's own jumps ahead of ours, where they belong.
            var probe = await runner.RunAsync(
                ["iptables", "-t", binding.Table, "-C", binding.Parent, "-j", binding.Name],
                allowFailure: true, ct: ct);

            if (!probe.Success)
            {
                await runner.RunAsync(["iptables", "-t", binding.Table, "-A", binding.Parent, "-j", binding.Name], ct: ct);
                logger.LogInformation("Linked {Table}/{Parent} -> {Chain}", binding.Table, binding.Parent, binding.Name);
            }
        }
    }

    private async Task ApplyFirewallAsync(RuleSet desired, CancellationToken ct)
    {
        foreach (var binding in Chains.All)
            await runner.RunAsync(["iptables", "-t", binding.Table, "-F", binding.Name], allowFailure: true, ct: ct);

        foreach (var rule in desired.Firewall)
        {
            var command = new List<string> { "iptables", "-t", rule.Table, "-A", rule.Chain };
            command.AddRange(rule.Args);
            var result = await runner.RunAsync(command, ct: ct);
            if (!result.Success)
                logger.LogError("Failed to install rule {Rule}: {Error}", rule, result.Output);
        }
    }

    /// <summary>
    /// Reconciles policy-routing rules. Only rules whose signature matches one we own are
    /// ever removed, so unrelated rules — Tailscale's marked rules, for instance — are left alone.
    /// </summary>
    private async Task ApplyIpRulesAsync(RuleSet desired, CancellationToken ct)
    {
        var live = await ReadIpRulesAsync(ct);

        foreach (var spec in desired.IpRules)
        {
            var signature = IpRuleSignature.From(spec.Selector);
            if (signature is null) continue;

            var matches = live.Where(r => signature == r.Signature).ToList();

            if (matches.Count == 1 && matches[0].Priority == spec.Priority)
                continue; // already exactly right

            foreach (var stale in matches)
            {
                await runner.RunAsync(
                    ["ip", "rule", "del", .. IpRuleSignature.ToSelector(stale.Signature), "priority", stale.Priority.ToString()],
                    allowFailure: true, ct: ct);
                logger.LogInformation("Removed ip rule at priority {Priority}: {Rule}", stale.Priority, stale.Body);
            }

            var add = await runner.RunAsync(
                ["ip", "rule", "add", .. spec.Selector, "priority", spec.Priority.ToString()], ct: ct);

            if (add.Success)
                logger.LogInformation("Installed ip rule {Rule}", spec.Render());
        }
    }

    private async Task ApplyIpRoutesAsync(RuleSet desired, CancellationToken ct)
    {
        // "replace" is idempotent, so these need no read-compare step.
        foreach (var route in desired.IpRoutes)
        {
            var result = await runner.RunAsync(
                ["ip", "route", "replace", .. route.Args, "table", route.Table.ToString()],
                allowFailure: true, ct: ct);

            if (!result.Success)
                logger.LogWarning("Could not install route {Route}: {Error}", route.Render(), result.Output);
        }
    }

    public async Task<RuleDiff> DiffAsync(RuleSet desired, CancellationToken ct = default)
    {
        var missing = new List<string>();
        var unexpected = new List<string>();
        var missingChains = new List<string>();

        foreach (var binding in Chains.All)
        {
            var result = await runner.RunAsync(
                ["iptables", "-t", binding.Table, "-S", binding.Name], allowFailure: true, ct: ct);

            if (!result.Success)
            {
                missingChains.Add($"{binding.Table}/{binding.Name}");
                continue;
            }

            var liveRules = result.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => l.StartsWith("-A ", StringComparison.Ordinal))
                .ToList();

            var wanted = desired.Firewall
                .Where(r => r.Table == binding.Table && r.Chain == binding.Name)
                .Select(r => r.Render())
                .ToList();

            // Compare canonical forms, not raw strings. iptables reorders arguments, expands
            // bare addresses to /32 and rewrites --set-mark as --set-xmark, so a literal
            // comparison flags every rule as drifted on every pass.
            var liveCanonical = liveRules.Select(RuleNormalizer.Canonicalize).ToHashSet(StringComparer.Ordinal);
            var wantedCanonical = wanted.Select(RuleNormalizer.Canonicalize).ToHashSet(StringComparer.Ordinal);

            missing.AddRange(wanted
                .Where(w => !liveCanonical.Contains(RuleNormalizer.Canonicalize(w)))
                .Select(w => $"[{binding.Table}] {w}"));

            unexpected.AddRange(liveRules
                .Where(l => !wantedCanonical.Contains(RuleNormalizer.Canonicalize(l)))
                .Select(l => $"[{binding.Table}] {l}"));
        }

        return new RuleDiff(missing, unexpected, missingChains);
    }

    /// <summary>Unlinks and deletes every chain we own. Used to back out cleanly.</summary>
    public async Task RemoveAllAsync(CancellationToken ct = default)
    {
        foreach (var binding in Chains.All)
        {
            await runner.RunAsync(["iptables", "-t", binding.Table, "-D", binding.Parent, "-j", binding.Name],
                allowFailure: true, ct: ct);
            await runner.RunAsync(["iptables", "-t", binding.Table, "-F", binding.Name], allowFailure: true, ct: ct);
            await runner.RunAsync(["iptables", "-t", binding.Table, "-X", binding.Name], allowFailure: true, ct: ct);
        }
        logger.LogWarning("Removed all PiRouter chains");
    }

    private async Task<List<LiveIpRule>> ReadIpRulesAsync(CancellationToken ct)
    {
        var result = await runner.RunAsync(["ip", "rule", "show"], allowFailure: true, ct: ct);
        var rules = new List<LiveIpRule>();
        if (!result.Success) return rules;

        foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = IpRulePattern.Match(line);
            if (!match.Success) continue;

            var body = match.Groups["body"].Value;
            var signature = IpRuleSignature.From(body);
            if (signature is null) continue;

            rules.Add(new LiveIpRule(int.Parse(match.Groups["prio"].Value, CultureInfo.InvariantCulture), body, signature));
        }
        return rules;
    }

    private sealed record LiveIpRule(int Priority, string Body, string Signature);
}
