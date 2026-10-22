using System.Windows.Media;
using Waikan.Infrastructure;

namespace Waikan.Tests;

public sealed class ColorUtilitiesTests
{
    [Theory]
    [InlineData("#FFF6F2EE", 255, 246, 242, 238)]
    [InlineData("#FF7A59", 255, 255, 122, 89)]
    [InlineData("FFD166", 255, 255, 209, 102)]
    public void Parses_Argb_And_Rgb_Hex(string value, byte a, byte r, byte g, byte b)
    {
        Assert.True(ColorUtilities.TryParse(value, out var color));
        Assert.Equal(Color.FromArgb(a, r, g, b), color);
    }

    [Theory]
    [InlineData("")]
    [InlineData("wat")]
    [InlineData("#12")]
    public void Rejects_Invalid_Values(string value)
    {
        Assert.False(ColorUtilities.TryParse(value, out _));
    }

    [Fact]
    public void RoundTrips_Color()
    {
        var color = Color.FromArgb(128, 12, 34, 56);
        var text = ColorUtilities.ToHex(color);
        Assert.True(ColorUtilities.TryParse(text, out var parsed));
        Assert.Equal(color, parsed);
    }
}
