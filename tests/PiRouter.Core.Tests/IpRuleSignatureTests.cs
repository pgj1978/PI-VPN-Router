using PiRouter.Core.Firewall;

namespace PiRouter.Core.Tests;

/// <summary>
/// The signature decides which live ip rules the applier is allowed to delete.
/// Every input below is real output taken from `ip rule show` on the router.
/// </summary>
public class IpRuleSignatureTests
{
    [Fact]
    public void Bypass_rule_is_recognised_from_hex_output()
    {
        // iproute2 prints marks in hex but accepts decimal, so 0x64 must equal 100.
        Assert.Equal("100->100", IpRuleSignature.From("from all fwmark 0x64 lookup 100"));
    }

    [Fact]
    public void Decimal_and_hex_forms_of_the_same_mark_agree()
    {
        Assert.Equal(
            IpRuleSignature.From("from all fwmark 0x64 lookup 100"),
            IpRuleSignature.From(["fwmark", "100", "lookup", "100"]));
    }

    [Fact]
    public void Wireguard_rule_is_distinguished_by_its_negation()
    {
        var wg = IpRuleSignature.From("not from all fwmark 0xca6d lookup 51821");

        Assert.Equal("not:51821->51821", wg);
        Assert.NotEqual(IpRuleSignature.From("from all fwmark 0xca6d lookup 51821"), wg);
    }

    [Fact]
    public void Tailscale_masked_rules_never_collide_with_ours()
    {
        // Tailscale installs "from all fwmark 0x80000/0xff0000 lookup main". Deleting one of
        // these because it looked like ours would break an unrelated VPN on the same box.
        var tailscale = IpRuleSignature.From("from all fwmark 0x80000/0xff0000 lookup main");

        Assert.Equal("0x80000/0xff0000->main", tailscale);
        Assert.NotEqual(IpRuleSignature.From("from all fwmark 0x64 lookup 100"), tailscale);
        Assert.DoesNotContain("524288", tailscale);
    }

    [Theory]
    [InlineData("from all lookup local")]
    [InlineData("from all lookup main suppress_prefixlength 0")]
    [InlineData("from all lookup 52")]
    [InlineData("from all lookup default")]
    public void Rules_without_a_fwmark_are_not_ours(string body) =>
        Assert.Null(IpRuleSignature.From(body));

    [Fact]
    public void Signature_round_trips_back_into_a_deletable_selector()
    {
        Assert.Equal(["fwmark", "100", "lookup", "100"],
            IpRuleSignature.ToSelector("100->100"));

        Assert.Equal(["not", "fwmark", "51821", "lookup", "51821"],
            IpRuleSignature.ToSelector("not:51821->51821"));
    }

    [Fact]
    public void Every_rule_from_the_live_router_classifies_correctly()
    {
        // Captured verbatim from `sudo ip rule show` on the Pi.
        string[] live =
        [
            "from all lookup local",
            "from all lookup main suppress_prefixlength 0",
            "from all fwmark 0x64 lookup 100",
            "from all fwmark 0x80000/0xff0000 lookup main",
            "from all fwmark 0x80000/0xff0000 lookup default",
            "from all fwmark 0x80000/0xff0000 unreachable",
            "from all lookup 52",
            "not from all fwmark 0xca6d lookup 51821",
            "from all lookup main",
            "from all lookup default",
        ];

        var ours = live.Select(IpRuleSignature.From)
                       .Where(s => s is "100->100" or "not:51821->51821")
                       .ToList();

        // Exactly the bypass rule and the wireguard rule, nothing belonging to Tailscale.
        Assert.Equal(2, ours.Count);
    }
}
