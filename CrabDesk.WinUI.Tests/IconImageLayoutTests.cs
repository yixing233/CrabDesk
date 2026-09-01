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
    [InlineData(24, 6)]
    [InlineData(48, 8)]
    [InlineData(96, 12)]
    public void SelectionCornerRadiusScalesWithinFluentBounds(float iconSize, float expected)
    {
        Assert.Equal(expected, DesktopItemVisualStyle.SelectionCornerRadius(iconSize));
    }
}
