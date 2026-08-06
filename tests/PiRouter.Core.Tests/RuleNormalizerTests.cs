using PiRouter.Core.Firewall;

namespace PiRouter.Core.Tests;

/// <summary>
/// Every pair below is a rule we wrote alongside the exact string iptables echoed back for
/// it, captured from the router during deployment. Without normalisation the drift detector
/// reported all six as both missing and unexpected, which would have had the reconciler
/// rewriting the firewall every 15 seconds forever.
/// </summary>
public class RuleNormalizerTests
{
    [Theory]
    // argument reordering
    [InlineData(
        "-A PIROUTER_MARK -i eth1 -d 192.168.20.0/24 -j RETURN",
        "-A PIROUTER_MARK -d 192.168.20.0/24 -i eth1 -j RETURN")]
    // bare address expanded to /32, and --set-mark rewritten as an xmark
    [InlineData(
        "-A PIROUTER_MARK -i eth1 -d 37.191.121.114 -j MARK --set-mark 100",
        "-A PIROUTER_MARK -d 37.191.121.114/32 -i eth1 -j MARK --set-xmark 0x64/0xffffffff")]
    // implicit "-m tcp" inserted after "-p tcp"
    [InlineData(
        "-A PIROUTER_MSS -o wg0 -p tcp --tcp-flags SYN,RST SYN -j TCPMSS --set-mss 1360",
        "-A PIROUTER_MSS -o wg0 -p tcp -m tcp --tcp-flags SYN,RST SYN -j TCPMSS --set-mss 1360")]
    // reordering plus prefix expansion together
    [InlineData(
        "-A PIROUTER_FWD -i eth1 -d 64.68.200.48 -o eth0 -j ACCEPT",
        "-A PIROUTER_FWD -d 64.68.200.48/32 -i eth1 -o eth0 -j ACCEPT")]
    // source address, same treatment
    [InlineData(
        "-A PIROUTER_FWD -i eth1 -s 192.168.20.159 -o eth0 -j ACCEPT",
        "-A PIROUTER_FWD -s 192.168.20.159/32 -i eth1 -o eth0 -j ACCEPT")]
    public void What_we_wrote_matches_what_iptables_echoes_back(string written, string readBack) =>
        Assert.True(RuleNormalizer.AreEquivalent(written, readBack),
            $"should be equivalent:\n  {RuleNormalizer.Canonicalize(written)}\n  {RuleNormalizer.Canonicalize(readBack)}");

    [Fact]
    public void Genuinely_different_rules_still_differ()
    {
        // The normaliser must not be so lax that real drift stops being visible.
        Assert.False(RuleNormalizer.AreEquivalent(
            "-A PIROUTER_FWD -i eth1 -o eth0 -j ACCEPT",
            "-A PIROUTER_FWD -i eth1 -o eth0 -j DROP"));

        Assert.False(RuleNormalizer.AreEquivalent(
            "-A PIROUTER_NAT -s 192.168.20.0/24 -o wg0 -j MASQUERADE",
            "-A PIROUTER_NAT -s 192.168.10.0/24 -o wg0 -j MASQUERADE"));

        Assert.False(RuleNormalizer.AreEquivalent(
            "-A PIROUTER_MARK -i eth1 -d 1.2.3.4 -j MARK --set-mark 100",
            "-A PIROUTER_MARK -i eth1 -d 1.2.3.4 -j MARK --set-mark 200"));
    }

    [Fact]
    public void A_different_interface_is_not_equivalent() =>
        Assert.False(RuleNormalizer.AreEquivalent(
            "-A PIROUTER_FWD -i eth1 -o eth0 -j ACCEPT",
            "-A PIROUTER_FWD -i eth0 -o eth1 -j ACCEPT"));

    [Fact]
    public void Negation_is_preserved()
    {
        Assert.False(RuleNormalizer.AreEquivalent(
            "-A PIROUTER_MARK -i eth1 ! -d 192.168.20.1 -j MARK --set-mark 100",
            "-A PIROUTER_MARK -i eth1 -d 192.168.20.1 -j MARK --set-mark 100"));
    }

    [Fact]
    public void Hex_and_decimal_marks_agree() =>
        Assert.True(RuleNormalizer.AreEquivalent(
            "-A X -m mark --mark 100 -j ACCEPT",
            "-A X -m mark --mark 0x64 -j ACCEPT"));

    [Fact]
    public void Canonicalize_is_stable() =>
        Assert.Equal(
            RuleNormalizer.Canonicalize("-A PIROUTER_FWD -i eth1 -o eth0 -j ACCEPT"),
            RuleNormalizer.Canonicalize(RuleNormalizer.Canonicalize("-A PIROUTER_FWD -i eth1 -o eth0 -j ACCEPT")));

    [Fact]
    public void Empty_input_is_handled() => Assert.Equal("", RuleNormalizer.Canonicalize("  "));
}
