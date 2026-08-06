using PiRouter.Core.Firewall;

namespace PiRouter.Core.Tests;

public class RuleCompilerTests
{
    private static RouterState State(
        bool vpnUp = true,
        bool killSwitch = false,
        string[]? deviceIps = null,
        string[]? domainIps = null,
        int? vpnTable = 51821,
        string? gateway = "192.168.5.1") => new()
        {
            LanInterface = "eth1",
            WanInterface = "eth0",
            VpnInterface = "wg0",
            LanIp = "192.168.20.1",
            LanNetwork = "192.168.20.0/24",
            WanGateway = gateway,
            VpnUp = vpnUp,
            VpnTableId = vpnTable,
            KillSwitchEnabled = killSwitch,
            BypassDeviceIps = deviceIps ?? [],
            BypassDomainIps = domainIps ?? [],
            TunnelExcludedPrefixes = ["192.168.0.0/16", "172.16.0.0/12"],
        };

    private static List<string> Chain(RuleSet set, string table, string chain) =>
        [.. set.Firewall.Where(r => r.Table == table && r.Chain == chain).Select(r => string.Join(' ', r.Args))];

    // ---------------------------------------------------------------- NAT

    [Fact]
    public void Nat_masquerades_the_configured_lan_network_onto_the_tunnel()
    {
        // The live router had this rule pinned to 192.168.10.0/24 while the LAN was
        // 192.168.20.0/24, so it matched nothing and no LAN traffic was ever translated
        // onto wg0. The network must come from state, never from a constant.
        var nat = Chain(RuleCompiler.Compile(State()), "nat", Chains.Nat);

        Assert.Contains("-s 192.168.20.0/24 -o wg0 -j MASQUERADE", nat);
    }

    [Fact]
    public void Nat_still_masquerades_to_wan_so_bypass_traffic_is_translated()
    {
        var nat = Chain(RuleCompiler.Compile(State()), "nat", Chains.Nat);

        Assert.Contains("-s 192.168.20.0/24 -o eth0 -j MASQUERADE", nat);
    }

    [Fact]
    public void Nat_omits_the_tunnel_rule_when_the_tunnel_is_down()
    {
        var nat = Chain(RuleCompiler.Compile(State(vpnUp: false)), "nat", Chains.Nat);

        Assert.DoesNotContain(nat, r => r.Contains("wg0"));
        Assert.Contains("-s 192.168.20.0/24 -o eth0 -j MASQUERADE", nat);
    }

    // ---------------------------------------------------------------- marking

    [Fact]
    public void Lan_local_traffic_returns_before_any_bypass_marking()
    {
        var mark = Chain(RuleCompiler.Compile(State(deviceIps: ["192.168.20.50"])), "mangle", Chains.Mark);

        Assert.Equal("-i eth1 -d 192.168.20.0/24 -j RETURN", mark[0]);
    }

    [Fact]
    public void Bypassed_device_is_marked_by_source_address()
    {
        var mark = Chain(RuleCompiler.Compile(State(deviceIps: ["192.168.20.50"])), "mangle", Chains.Mark);

        Assert.Contains("-i eth1 -s 192.168.20.50 -j MARK --set-mark 100", mark);
    }

    [Fact]
    public void Bypassed_domain_is_marked_by_destination_address()
    {
        var mark = Chain(RuleCompiler.Compile(State(domainIps: ["64.68.200.48"])), "mangle", Chains.Mark);

        Assert.Contains("-i eth1 -d 64.68.200.48 -j MARK --set-mark 100", mark);
    }

    [Fact]
    public void Every_a_record_for_a_domain_is_marked_not_just_the_first()
    {
        // Domain bypass used to keep only the first resolved address, so multi-homed
        // services worked intermittently depending on which IP the client happened to use.
        var mark = Chain(
            RuleCompiler.Compile(State(domainIps: ["64.68.200.48", "37.191.121.114", "1.2.3.4"])),
            "mangle", Chains.Mark);

        Assert.Contains("-i eth1 -d 64.68.200.48 -j MARK --set-mark 100", mark);
        Assert.Contains("-i eth1 -d 37.191.121.114 -j MARK --set-mark 100", mark);
        Assert.Contains("-i eth1 -d 1.2.3.4 -j MARK --set-mark 100", mark);
    }

    [Fact]
    public void No_marking_rule_embeds_the_routers_own_address()
    {
        // Regression: .ai/BUGFIX_IP_CHANGE_BYPASS_20260102.md
        // Rules used to carry "! -d <router ip>". When the router's LAN address changed,
        // stale rules matched almost everything and bypass jammed permanently on.
        // The router address must not appear in a per-device rule at all.
        var mark = Chain(
            RuleCompiler.Compile(State(deviceIps: ["192.168.20.50", "192.168.20.51"])),
            "mangle", Chains.Mark);

        Assert.DoesNotContain(mark.Skip(1), r => r.Contains("192.168.20.1"));
        Assert.DoesNotContain(mark, r => r.Contains("!"));
    }

    [Fact]
    public void Removing_a_bypass_leaves_no_trace_of_it()
    {
        // Regression: .ai/BUGFIX_BYPASS_REGRESSION_20260102_PM.md
        // Turning bypass off used to issue a targeted "iptables -D" that could fail silently,
        // stranding the MARK rule so traffic stayed marked with no matching bypass route and
        // was then dropped by the FORWARD policy — the device lost internet entirely.
        // Compilation is total, so "off" simply means the rule was never emitted.
        var on = RuleCompiler.Compile(State(deviceIps: ["192.168.20.50"]));
        var off = RuleCompiler.Compile(State(deviceIps: []));

        Assert.Contains(Chain(on, "mangle", Chains.Mark), r => r.Contains("192.168.20.50"));
        Assert.DoesNotContain(Chain(off, "mangle", Chains.Mark), r => r.Contains("192.168.20.50"));
        Assert.DoesNotContain(off.Firewall, r => string.Join(' ', r.Args).Contains("192.168.20.50"));
    }

    // ---------------------------------------------------------------- kill switch

    [Fact]
    public void Kill_switch_off_allows_lan_out_of_the_wan()
    {
        var fwd = Chain(RuleCompiler.Compile(State(killSwitch: false)), "filter", Chains.Forward);

        Assert.Contains("-i eth1 -o eth0 -j ACCEPT", fwd);
        Assert.DoesNotContain("-i eth1 -o eth0 -j DROP", fwd);
    }

    [Fact]
    public void Kill_switch_on_drops_lan_out_of_the_wan()
    {
        var fwd = Chain(RuleCompiler.Compile(State(killSwitch: true)), "filter", Chains.Forward);

        Assert.Contains("-i eth1 -o eth0 -j DROP", fwd);
        Assert.DoesNotContain("-i eth1 -o eth0 -j ACCEPT", fwd);
    }

    [Fact]
    public void Kill_switch_blocks_leaks_even_while_the_tunnel_is_healthy()
    {
        // With the kill switch on, anything reaching for the WAN that isn't explicitly
        // bypassed is a leak regardless of tunnel state.
        var fwd = Chain(RuleCompiler.Compile(State(vpnUp: true, killSwitch: true)), "filter", Chains.Forward);

        Assert.Contains("-i eth1 -o eth0 -j DROP", fwd);
        Assert.Contains("-i eth1 -o wg0 -j ACCEPT", fwd);
    }

    [Fact]
    public void Kill_switch_does_not_cut_off_bypassed_devices()
    {
        // The interaction that is easiest to get wrong: the user asked for this device to go
        // direct, so the kill switch must not contradict that.
        var set = RuleCompiler.Compile(State(vpnUp: false, killSwitch: true, deviceIps: ["192.168.20.50"]));
        var fwd = Chain(set, "filter", Chains.Forward);

        var accept = fwd.IndexOf("-i eth1 -s 192.168.20.50 -o eth0 -j ACCEPT");
        var drop = fwd.IndexOf("-i eth1 -o eth0 -j DROP");

        Assert.True(accept >= 0, "bypassed device must still be accepted out of the WAN");
        Assert.True(drop >= 0, "kill switch drop must be present");
        Assert.True(accept < drop, "the bypass accept must be evaluated before the kill-switch drop");
    }

    [Fact]
    public void Kill_switch_does_not_cut_off_bypassed_domains()
    {
        var set = RuleCompiler.Compile(State(vpnUp: false, killSwitch: true, domainIps: ["64.68.200.48"]));
        var fwd = Chain(set, "filter", Chains.Forward);

        var accept = fwd.IndexOf("-i eth1 -d 64.68.200.48 -o eth0 -j ACCEPT");
        var drop = fwd.IndexOf("-i eth1 -o eth0 -j DROP");

        Assert.True(accept >= 0 && accept < drop);
    }

    [Fact]
    public void Return_traffic_to_lan_is_always_accepted_first()
    {
        var fwd = Chain(RuleCompiler.Compile(State(killSwitch: true)), "filter", Chains.Forward);

        Assert.Equal("-o eth1 -m conntrack --ctstate RELATED,ESTABLISHED -j ACCEPT", fwd[0]);
    }

    // ---------------------------------------------------------------- policy routing

    [Fact]
    public void Bypass_ip_rule_outranks_the_wireguard_rule()
    {
        // wg-quick installs "not fwmark <t> lookup <t>" at priority 0, which beats the bypass
        // rule and drags bypass traffic into the tunnel. It has to be relocated below it.
        var set = RuleCompiler.Compile(State());

        var bypass = set.IpRules.Single(r => r.Selector.Contains("fwmark") && !r.Selector.Contains("not"));
        var wg = set.IpRules.Single(r => r.Selector.Contains("not"));

        Assert.True(bypass.Priority < wg.Priority,
            $"bypass rule at {bypass.Priority} must outrank the wireguard rule at {wg.Priority}");
    }

    [Fact]
    public void Bypass_table_routes_straight_to_the_upstream_gateway()
    {
        var set = RuleCompiler.Compile(State());

        var route = set.IpRoutes.Single(r => r.Table == 100);
        Assert.Equal("default via 192.168.5.1 dev eth0", string.Join(' ', route.Args));
    }

    [Fact]
    public void No_bypass_route_is_emitted_when_the_gateway_is_unknown()
    {
        // Better to emit nothing and let a diagnostic report it than to install a route
        // that silently black-holes every bypassed device.
        var set = RuleCompiler.Compile(State(gateway: null));

        Assert.DoesNotContain(set.IpRoutes, r => r.Table == 100);
    }

    [Fact]
    public void Local_prefixes_are_excluded_from_the_tunnel()
    {
        var set = RuleCompiler.Compile(State());

        var throws = set.IpRoutes.Where(r => r.Table == 51821).Select(r => string.Join(' ', r.Args)).ToList();
        Assert.Contains("throw 192.168.0.0/16", throws);
        Assert.Contains("throw 172.16.0.0/12", throws);
    }

    // ---------------------------------------------------------------- MSS

    [Fact]
    public void Mss_is_clamped_only_for_traffic_entering_the_tunnel()
    {
        var mss = Chain(RuleCompiler.Compile(State()), "mangle", Chains.Mss);

        Assert.Equal(["-o wg0 -p tcp --tcp-flags SYN,RST SYN -j TCPMSS --set-mss 1360"], mss);
    }

    [Fact]
    public void Mss_clamping_disappears_with_the_tunnel()
    {
        Assert.Empty(Chain(RuleCompiler.Compile(State(vpnUp: false)), "mangle", Chains.Mss));
    }

    // ---------------------------------------------------------------- determinism

    [Fact]
    public void Compilation_is_deterministic_regardless_of_input_ordering()
    {
        // The reconciler skips work when the fingerprint is unchanged. If ordering leaked
        // through, it would rewrite the firewall on every single tick.
        var a = RuleCompiler.Compile(State(deviceIps: ["192.168.20.50", "192.168.20.12"], domainIps: ["9.9.9.9", "1.1.1.1"]));
        var b = RuleCompiler.Compile(State(deviceIps: ["192.168.20.12", "192.168.20.50"], domainIps: ["1.1.1.1", "9.9.9.9"]));

        Assert.Equal(a.Fingerprint(), b.Fingerprint());
    }

    [Fact]
    public void Duplicate_addresses_collapse_to_a_single_rule()
    {
        // Two devices resolving to the same address, or a domain listed twice, must not
        // produce two identical rules. Duplicate accumulation is the exact failure that
        // left 4 copies of "-i eth0 -o wg0 -j ACCEPT" in the live FORWARD chain.
        var mark = Chain(
            RuleCompiler.Compile(State(deviceIps: ["192.168.20.50", "192.168.20.50"])),
            "mangle", Chains.Mark);

        Assert.Single(mark, r => r.Contains("192.168.20.50"));
    }

    [Fact]
    public void Fingerprint_changes_when_behaviour_changes()
    {
        Assert.NotEqual(
            RuleCompiler.Compile(State(killSwitch: false)).Fingerprint(),
            RuleCompiler.Compile(State(killSwitch: true)).Fingerprint());
    }

    [Fact]
    public void Only_owned_chains_are_ever_emitted()
    {
        // Nothing may target a built-in chain. Docker shares FORWARD, PREROUTING and
        // POSTROUTING with us and must never be disturbed.
        var set = RuleCompiler.Compile(State(killSwitch: true, deviceIps: ["192.168.20.50"], domainIps: ["1.1.1.1"]));

        var owned = Chains.All.Select(c => c.Name).ToHashSet();
        Assert.All(set.Firewall, r => Assert.Contains(r.Chain, owned));
    }
}
