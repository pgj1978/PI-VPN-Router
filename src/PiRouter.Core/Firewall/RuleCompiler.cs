namespace PiRouter.Core.Firewall;

/// <summary>
/// Turns a <see cref="RouterState"/> into the complete set of firewall rules, ip rules and
/// routes that state implies.
///
/// This is a pure function: no I/O, no ambient state, deterministic ordering. That is the
/// whole point — the routing behaviour that used to only be observable by SSH-ing into a
/// live router and reading `iptables -S` is now something we can assert on in a unit test.
/// </summary>
public static class RuleCompiler
{
    public static RuleSet Compile(RouterState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new RuleSet
        {
            Firewall = [.. CompileMark(state), .. CompileMss(state), .. CompileForward(state), .. CompileNat(state)],
            IpRules = CompileIpRules(state),
            IpRoutes = CompileIpRoutes(state),
        };
    }

    /// <summary>
    /// mangle PREROUTING. Marks packets that should skip the tunnel so that the policy
    /// routing rule sends them to the bypass table instead.
    /// </summary>
    private static List<FirewallRule> CompileMark(RouterState s)
    {
        var rules = new List<FirewallRule>();

        // Traffic that stays on the LAN (including traffic to the router itself) is never
        // marked. Doing this once up front is why individual rules below don't each need
        // a "! -d <router ip>" qualifier, which is what the old code got wrong whenever
        // the router's own address changed.
        rules.Add(Mangle(Chains.Mark, "-i", s.LanInterface, "-d", s.LanNetwork, "-j", "RETURN"));

        foreach (var ip in Ordered(s.BypassDeviceIps))
            rules.Add(Mangle(Chains.Mark, "-i", s.LanInterface, "-s", ip,
                "-j", "MARK", "--set-mark", s.BypassMark.ToString()));

        foreach (var ip in Ordered(s.BypassDomainIps))
            rules.Add(Mangle(Chains.Mark, "-i", s.LanInterface, "-d", ip,
                "-j", "MARK", "--set-mark", s.BypassMark.ToString()));

        return rules;
    }

    /// <summary>
    /// mangle FORWARD. Clamps TCP MSS for traffic entering the tunnel, which prevents the
    /// large-packet black holes WireGuard's reduced MTU otherwise causes.
    ///
    /// Scoped to "-o vpn" only. The previous implementation clamped every packet in FORWARD,
    /// OUTPUT and POSTROUTING unconditionally, which needlessly shrank MSS on bypass traffic
    /// that was going straight out of the WAN at full MTU.
    /// </summary>
    private static List<FirewallRule> CompileMss(RouterState s)
    {
        if (!s.VpnUp) return [];

        return
        [
            new FirewallRule("mangle", Chains.Mss,
                ["-o", s.VpnInterface, "-p", "tcp", "--tcp-flags", "SYN,RST", "SYN",
                 "-j", "TCPMSS", "--set-mss", s.VpnMss.ToString()])
        ];
    }

    /// <summary>
    /// filter FORWARD. Order is load-bearing: bypass accepts must precede the kill-switch
    /// drop, or enabling the kill switch would also cut off the devices the user explicitly
    /// asked to send direct.
    /// </summary>
    private static List<FirewallRule> CompileForward(RouterState s)
    {
        var rules = new List<FirewallRule>
        {
            // Return traffic back to LAN clients, whichever path it came in on.
            Filter(Chains.Forward, "-o", s.LanInterface,
                "-m", "conntrack", "--ctstate", "RELATED,ESTABLISHED", "-j", "ACCEPT"),
        };

        // Explicitly permitted direct-to-internet traffic.
        foreach (var ip in Ordered(s.BypassDeviceIps))
            rules.Add(Filter(Chains.Forward, "-i", s.LanInterface, "-s", ip, "-o", s.WanInterface, "-j", "ACCEPT"));

        foreach (var ip in Ordered(s.BypassDomainIps))
            rules.Add(Filter(Chains.Forward, "-i", s.LanInterface, "-d", ip, "-o", s.WanInterface, "-j", "ACCEPT"));

        // Everything else goes down the tunnel when there is one.
        if (s.VpnUp)
            rules.Add(Filter(Chains.Forward, "-i", s.LanInterface, "-o", s.VpnInterface, "-j", "ACCEPT"));

        // The kill switch itself. With it on, anything still reaching for the WAN that wasn't
        // matched by a bypass rule above is a leak and gets dropped — including when the
        // tunnel is up, not just when it has failed.
        rules.Add(s.BlockNonBypassedWanEgress
            ? Filter(Chains.Forward, "-i", s.LanInterface, "-o", s.WanInterface, "-j", "DROP")
            : Filter(Chains.Forward, "-i", s.LanInterface, "-o", s.WanInterface, "-j", "ACCEPT"));

        return rules;
    }

    /// <summary>
    /// nat POSTROUTING. Source NAT keyed on the configured LAN network. The live router had
    /// this rule hardcoded to 192.168.10.0/24 against a 192.168.20.0/24 LAN, so it matched
    /// nothing and LAN traffic was never translated onto the tunnel.
    /// </summary>
    private static List<FirewallRule> CompileNat(RouterState s)
    {
        var rules = new List<FirewallRule>();

        if (s.VpnUp)
            rules.Add(Nat(Chains.Nat, "-s", s.LanNetwork, "-o", s.VpnInterface, "-j", "MASQUERADE"));

        rules.Add(Nat(Chains.Nat, "-s", s.LanNetwork, "-o", s.WanInterface, "-j", "MASQUERADE"));

        return rules;
    }

    private static List<IpRuleSpec> CompileIpRules(RouterState s)
    {
        var rules = new List<IpRuleSpec>
        {
            // Marked packets consult the bypass table, which routes straight to the upstream gateway.
            new(s.BypassRulePriority, ["fwmark", s.BypassMark.ToString(), "lookup", s.BypassMark.ToString()]),
        };

        // wg-quick installs its "not fwmark <t> lookup <t>" rule at priority 0, which outranks
        // the bypass rule above and sends bypass traffic into the tunnel anyway. We relocate it
        // below the bypass rule. This is the reason bypass appeared to do nothing.
        if (s is { VpnUp: true, VpnTableId: > 0 })
        {
            var table = s.VpnTableId.Value.ToString();
            rules.Add(new IpRuleSpec(s.VpnRulePriority, ["not", "fwmark", table, "lookup", table]));
        }

        return rules;
    }

    private static List<IpRouteSpec> CompileIpRoutes(RouterState s)
    {
        var routes = new List<IpRouteSpec>();

        // Bypass table: everything marked goes straight out of the WAN.
        // Omitted when the gateway is unknown; a diagnostic reports that rather than
        // installing a route that would black-hole traffic.
        if (!string.IsNullOrWhiteSpace(s.WanGateway))
            routes.Add(new IpRouteSpec(s.BypassMark,
                ["default", "via", s.WanGateway, "dev", s.WanInterface]));

        // Keep LAN and container traffic out of the tunnel by falling back to the main table.
        if (s is { VpnUp: true, VpnTableId: > 0 })
            foreach (var prefix in Ordered(s.TunnelExcludedPrefixes))
                routes.Add(new IpRouteSpec(s.VpnTableId.Value, ["throw", prefix]));

        return routes;
    }

    // Sorted and de-duplicated so that compilation is deterministic: the same logical state
    // must always produce a byte-identical ruleset, otherwise the reconciler's fingerprint
    // would churn and it would rewrite the firewall on every tick.
    private static IEnumerable<string> Ordered(IEnumerable<string> values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v))
              .Select(v => v.Trim())
              .Distinct(StringComparer.OrdinalIgnoreCase)
              .OrderBy(v => v, StringComparer.Ordinal);

    private static FirewallRule Mangle(string chain, params string[] args) => new("mangle", chain, args);
    private static FirewallRule Filter(string chain, params string[] args) => new("filter", chain, args);
    private static FirewallRule Nat(string chain, params string[] args) => new("nat", chain, args);
}
