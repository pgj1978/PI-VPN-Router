using PiRouter.Core.Net;

namespace PiRouter.Core.Tests;

public class CidrTests
{
    [Theory]
    [InlineData("192.168.20.1/24", "192.168.20.1")]
    [InlineData("10.0.0.5/8", "10.0.0.5")]
    [InlineData("192.168.20.1", "192.168.20.1")]
    public void AddressOf_strips_the_prefix(string input, string expected) =>
        Assert.Equal(expected, Cidr.AddressOf(input));

    [Theory]
    [InlineData("192.168.20.1/24", "192.168.20.0/24")]
    [InlineData("192.168.20.254/24", "192.168.20.0/24")]
    [InlineData("10.14.0.2/16", "10.14.0.0/16")]
    [InlineData("172.16.5.4/12", "172.16.0.0/12")]
    public void NetworkOf_masks_the_host_bits(string input, string expected) =>
        Assert.Equal(expected, Cidr.NetworkOf(input));

    [Theory]
    [InlineData(24, "255.255.255.0")]
    [InlineData(16, "255.255.0.0")]
    [InlineData(8, "255.0.0.0")]
    [InlineData(32, "255.255.255.255")]
    [InlineData(0, "0.0.0.0")]
    public void PrefixToMask_round_trips(int prefix, string mask)
    {
        Assert.Equal(mask, Cidr.PrefixToMask(prefix));
        Assert.Equal(prefix, Cidr.MaskToPrefix(mask));
    }

    [Fact]
    public void MaskToPrefix_rejects_a_mask_with_holes() =>
        Assert.Equal(-1, Cidr.MaskToPrefix("255.0.255.0"));

    [Fact]
    public void MaskToPrefix_rejects_nonsense() =>
        Assert.Equal(-1, Cidr.MaskToPrefix("not-a-mask"));

    [Theory]
    [InlineData("192.168.20.0/24", "192.168.20.50", true)]
    [InlineData("192.168.20.0/24", "192.168.21.50", false)]
    [InlineData("192.168.0.0/16", "192.168.20.50", true)]
    [InlineData("172.16.0.0/12", "172.20.0.1", true)]
    [InlineData("172.16.0.0/12", "172.32.0.1", false)]
    public void Contains_matches_on_the_network_portion(string cidr, string address, bool expected) =>
        Assert.Equal(expected, Cidr.Contains(cidr, address));

    [Fact]
    public void Contains_is_false_for_unparseable_input() =>
        Assert.False(Cidr.Contains("192.168.20.0/24", "wat"));

    [Theory]
    [InlineData("192.168.20.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("256.1.1.1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidIpv4_screens_user_input(string? value, bool expected) =>
        Assert.Equal(expected, Cidr.IsValidIpv4(value));

    [Fact]
    public void IsValidIpv4_rejects_a_bare_integer()
    {
        // IPAddress.TryParse historically accepted these; a DHCP reservation of "1" is a bug.
        Assert.False(Cidr.IsValidIpv4("1"));
    }
}
