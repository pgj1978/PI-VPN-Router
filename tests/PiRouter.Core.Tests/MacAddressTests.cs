using PiRouter.Core.Net;

namespace PiRouter.Core.Tests;

public class MacAddressTests
{
    [Theory]
    [InlineData("00:d8:61:34:29:8a")]
    [InlineData("00:D8:61:34:29:8A")]
    [InlineData("00-d8-61-34-29-8a")]
    [InlineData("00d8.6134.298a")]
    [InlineData("00d861 34298a")]
    [InlineData("00d861 34298A")]
    public void All_common_spellings_normalise_to_the_same_key(string input)
    {
        Assert.True(MacAddress.TryNormalise(input, out var normalised));
        Assert.Equal("00:d8:61:34:29:8a", normalised);
    }

    [Fact]
    public void Url_encoded_input_is_decoded()
    {
        // The UI puts MACs in the path, so they arrive percent-encoded.
        Assert.Equal("00:d8:61:34:29:8a", MacAddress.Normalise("00%3Ad8%3A61%3A34%3A29%3A8a"));
    }

    [Theory]
    [InlineData("00:d8:61:34:29")]        // too short
    [InlineData("00:d8:61:34:29:8a:bb")]  // too long
    [InlineData("zz:d8:61:34:29:8a")]     // not hex
    [InlineData("not-a-mac")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rejects_anything_that_is_not_a_mac(string? input)
    {
        Assert.False(MacAddress.TryNormalise(input, out _));
        Assert.Null(MacAddress.Normalise(input));
    }

    [Fact]
    public void Normalisation_is_idempotent()
    {
        var once = MacAddress.Normalise("00:D8:61:34:29:8A")!;
        Assert.Equal(once, MacAddress.Normalise(once));
    }
}
