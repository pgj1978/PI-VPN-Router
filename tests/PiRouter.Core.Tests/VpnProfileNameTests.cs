using PiRouter.Core.Services;

namespace PiRouter.Core.Tests;

public class VpnProfileNameTests
{
    [Theory]
    [InlineData("wg-london-st005")]
    [InlineData("wg_manchester")]
    [InlineData("profile.1")]
    [InlineData("A")]
    public void Accepts_ordinary_profile_names(string name) =>
        Assert.True(VpnProfileName.IsValid(name));

    [Theory]
    [InlineData("../../../etc/cron.d/evil")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("/etc/passwd")]
    [InlineData("wg london")]           // space
    [InlineData("wg;reboot")]           // shell metacharacter
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rejects_anything_that_could_escape_or_inject(string? name) =>
        Assert.False(VpnProfileName.IsValid(name));

    [Fact]
    public void Rejects_an_embedded_null_byte() =>
        Assert.False(VpnProfileName.IsValid("wg" + (char)0 + "null"));

    [Fact]
    public void Rejects_absurdly_long_names() =>
        Assert.False(VpnProfileName.IsValid(new string('a', VpnProfileName.MaxLength + 1)));

    [Fact]
    public void ResolvePath_keeps_the_file_inside_the_profile_directory()
    {
        var path = VpnProfileName.ResolvePath("/app/config/vpn_profiles", "wg-london-st005");

        Assert.EndsWith("wg-london-st005.conf", path);
        Assert.Contains("vpn_profiles", path);
    }

    [Fact]
    public void ResolvePath_refuses_a_traversal_attempt()
    {
        // This is the exact shape of request the old AddVpnProfile endpoint would have honoured.
        Assert.Throws<ArgumentException>(() =>
            VpnProfileName.ResolvePath("/app/config/vpn_profiles", "../../../etc/cron.d/evil"));
    }
}
