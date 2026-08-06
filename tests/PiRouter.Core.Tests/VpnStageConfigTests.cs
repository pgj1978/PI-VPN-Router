using PiRouter.Core.Services;

namespace PiRouter.Core.Tests;

/// <summary>
/// A profile's DNS line has to be stripped before wg-quick sees it. Deployment found this
/// the hard way: wg-quick called resolvconf, resolvconf cannot work in a container, and
/// wg-quick treats that as fatal and deletes the interface it just created.
/// </summary>
public class VpnStageConfigTests
{
    // A real Surfshark profile, private key redacted.
    private const string SurfsharkProfile = """
        [Interface]
        Address = 10.14.0.2/16
        PrivateKey = REDACTED=
        DNS = 162.252.172.57, 149.154.159.92
        MTU = 1420

        [Peer]
        PublicKey = QWhiKTxKWp9wo3blPDcMdA1Y/Vn69u2d8WQKQMTuoWw=
        AllowedIPs = 0.0.0.0/0
        Endpoint = uk-lon-st005.prod.surfshark.com:51820
        PersistentKeepalive = 25
        """;

    [Fact]
    public void Strips_the_dns_line()
    {
        var staged = VpnService.StageConfig(SurfsharkProfile);

        Assert.DoesNotContain("DNS", staged, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("162.252.172.57", staged);
    }

    [Fact]
    public void Keeps_everything_the_tunnel_actually_needs()
    {
        var staged = VpnService.StageConfig(SurfsharkProfile);

        Assert.Contains("[Interface]", staged);
        Assert.Contains("Address = 10.14.0.2/16", staged);
        Assert.Contains("PrivateKey = REDACTED=", staged);
        Assert.Contains("MTU = 1420", staged);
        Assert.Contains("[Peer]", staged);
        Assert.Contains("Endpoint = uk-lon-st005.prod.surfshark.com:51820", staged);
        Assert.Contains("PersistentKeepalive = 25", staged);
    }

    [Fact]
    public void Does_not_strip_keys_that_merely_start_with_dns()
    {
        // Guard against a naive StartsWith("DNS") also eating a hypothetical DNSSearch key.
        var staged = VpnService.StageConfig("[Interface]\nDNSSomethingElse\nMTU = 1420\n");

        Assert.Contains("DNSSomethingElse", staged);
    }

    [Fact]
    public void Handles_crlf_profiles()
    {
        // Profiles pasted through the UI from Windows arrive with CRLF.
        var staged = VpnService.StageConfig("[Interface]\r\nDNS = 1.1.1.1\r\nMTU = 1420\r\n");

        Assert.DoesNotContain("DNS", staged, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\r", staged);
        Assert.Contains("MTU = 1420", staged);
    }

    [Fact]
    public void A_profile_without_dns_is_unchanged_apart_from_trailing_whitespace()
    {
        const string input = "[Interface]\nAddress = 10.0.0.1/24\nMTU = 1420\n";

        Assert.Equal(input, VpnService.StageConfig(input));
    }
}
