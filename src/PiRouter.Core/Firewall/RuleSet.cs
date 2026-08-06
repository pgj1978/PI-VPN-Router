namespace PiRouter.Core.Firewall;

/// <summary>The four chains PiRouter owns. Nothing outside this list is ever modified.</summary>
public static class Chains
{
    /// <summary>mangle PREROUTING: marks bypass traffic so policy routing sends it out the WAN.</summary>
    public const string Mark = "PIROUTER_MARK";

    /// <summary>mangle FORWARD: TCP MSS clamping for traffic entering the tunnel.</summary>
    public const string Mss = "PIROUTER_MSS";

    /// <summary>filter FORWARD: what is allowed to be forwarded, including the kill switch.</summary>
    public const string Forward = "PIROUTER_FWD";

    /// <summary>nat POSTROUTING: source NAT for LAN traffic leaving via tunnel or WAN.</summary>
    public const string Nat = "PIROUTER_NAT";

    /// <summary>Each owned chain and the built-in chain that jumps into it.</summary>
    public static readonly IReadOnlyList<ChainBinding> All =
    [
        new("mangle", Mark, "PREROUTING"),
        new("mangle", Mss, "FORWARD"),
        new("filter", Forward, "FORWARD"),
        new("nat", Nat, "POSTROUTING"),
    ];
}

/// <param name="Table">iptables table, e.g. "filter".</param>
/// <param name="Name">Our chain, e.g. "PIROUTER_FWD".</param>
/// <param name="Parent">Built-in chain that jumps into ours.</param>
public sealed record ChainBinding(string Table, string Name, string Parent);

/// <summary>A single iptables rule. <paramref name="Args"/> excludes the -A and chain name.</summary>
public sealed record FirewallRule(string Table, string Chain, IReadOnlyList<string> Args)
{
    /// <summary>Rendered as it would appear in iptables-save output, for logging and drift diffs.</summary>
    public string Render() => $"-A {Chain} {string.Join(' ', Args)}";

    public override string ToString() => $"[{Table}] {Render()}";
}

/// <param name="Priority">Lower numbers take precedence.</param>
public sealed record IpRuleSpec(int Priority, IReadOnlyList<string> Selector)
{
    public string Render() => $"{Priority}: {string.Join(' ', Selector)}";
}

/// <param name="Table">Routing table number.</param>
/// <param name="Args">Route spec, e.g. ["default","via","192.168.5.1","dev","eth0"].</param>
public sealed record IpRouteSpec(int Table, IReadOnlyList<string> Args)
{
    public string Render() => $"table {Table}: {string.Join(' ', Args)}";
}

/// <summary>
/// The complete desired network state. Produced by <see cref="RuleCompiler"/> and applied
/// wholesale — our chains are flushed and rebuilt from this on every apply, which is what
/// makes duplicate-rule accumulation structurally impossible.
/// </summary>
public sealed record RuleSet
{
    public IReadOnlyList<FirewallRule> Firewall { get; init; } = [];
    public IReadOnlyList<IpRuleSpec> IpRules { get; init; } = [];
    public IReadOnlyList<IpRouteSpec> IpRoutes { get; init; } = [];

    /// <summary>Stable fingerprint used to skip no-op reconciles.</summary>
    public string Fingerprint()
    {
        var text = string.Join('\n',
            Firewall.Select(r => r.ToString())
                .Concat(IpRules.Select(r => r.Render()))
                .Concat(IpRoutes.Select(r => r.Render())));
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(hash)[..16];
    }

    public IEnumerable<string> Describe() =>
        Firewall.Select(r => r.ToString())
            .Concat(IpRules.Select(r => $"[ip rule] {r.Render()}"))
            .Concat(IpRoutes.Select(r => $"[ip route] {r.Render()}"));
}
