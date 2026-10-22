using System.Drawing;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class IconImageLayoutTests
{
    [Fact]
    public void ContainPreservesLandscapeAspectRatio()
    {
        using var image = new Bitmap(400, 200);
        var result = IconImageLayout.Contain(image, new RectangleF(10, 20, 48, 48));

        Assert.Equal(48, result.Width, 3);
        Assert.Equal(24, result.Height, 3);
        Assert.Equal(10, result.X, 3);
        Assert.Equal(32, result.Y, 3);
    }

    [Fact]
    public void ContainPreservesPortraitAspectRatio()
    {
        using var image = new Bitmap(200, 400);
        var result = IconImageLayout.Contain(image, new RectangleF(0, 0, 48, 48));

        Assert.Equal(24, result.Width, 3);
        Assert.Equal(48, result.Height, 3);
        Assert.Equal(12, result.X, 3);
        Assert.Equal(0, result.Y, 3);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(8, 8)]
    [InlineData(12, 12)]
    [InlineData(20, 20)]
    [InlineData(25, 20)]
    public void HighlightCornerRadiusUsesTheConfiguredValueWithinItsRange(double configuredRadius, float expected)
    {
        Assert.Equal(expected, DesktopItemVisualStyle.HighlightCornerRadius(configuredRadius));
    }
}
