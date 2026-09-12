using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class BoxIconZoomGridTests
{
    [Theory]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(42)]
    [InlineData(64)]
    [InlineData(80)]
    [InlineData(96)]
    public void ZoomPreservesTextAndPaddingInsteadOfScalingBlankSpace(double iconSize)
    {
        var horizontal = DesktopItemLayoutEngine.ScaleIconSpacing(76, iconSize);
        var vertical = DesktopItemLayoutEngine.ScaleIconSpacing(80, iconSize);
        Assert.Equal(iconSize + 34, DesktopItemLayoutEngine.GetGridCellWidth(iconSize, horizontal));
        Assert.Equal(iconSize + 38, DesktopItemLayoutEngine.GetGridCellHeight(iconSize, vertical));
    }

    [Theory]
    [InlineData(42, 100)]
    [InlineData(64, 122)]
    [InlineData(96, 154)]
    public void CustomSpacingKeepsItsExtraPadding(double iconSize, double expected)
    {
        Assert.Equal(expected, DesktopItemLayoutEngine.ScaleIconSpacing(100, iconSize));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(42)]
    [InlineData(64)]
    [InlineData(96)]
    public void FullVisibleAndScrollLayoutsAgreeAfterZoom(double iconSize)
    {
        var body = new LayoutRect(8, 76, 388, 201);
        var horizontal = DesktopItemLayoutEngine.ScaleIconSpacing(76, iconSize);
        var vertical = DesktopItemLayoutEngine.ScaleIconSpacing(80, iconSize);
        var full = DesktopItemLayoutEngine.Calculate(BoxViewMode.Grid, body, 50,
            iconSize, horizontal, vertical, 177);
        var visible = DesktopItemLayoutEngine.CalculateVisible(BoxViewMode.Grid, body, 50,
            iconSize, horizontal, vertical, 177);
        Assert.Equal(full.ScrollOffset, visible.ScrollOffset);
        Assert.Equal(full.MaxScroll, visible.MaxScroll);
        Assert.Equal(full.MaxScroll, DesktopItemLayoutEngine.GetScrollExtent(
            BoxViewMode.Grid, body, 50, iconSize, horizontal, vertical));
        foreach (var entry in visible.Items)
        {
            Assert.Equal(full.Items[entry.Index], entry.Bounds);
            Assert.InRange(entry.Bounds.Width, iconSize + 34, iconSize + 50);
            Assert.True(entry.Bounds.X + entry.Bounds.Width <= body.X + body.Width + 0.001);
        }
    }

    [Fact]
    public void LargeIconsDoNotStretchIntoOversizedCardsWhenOnlyTwoColumnsFit()
    {
        var result = DesktopItemLayoutEngine.Calculate(BoxViewMode.Grid,
            new LayoutRect(8, 10, 388, 300), 3, 96,
            DesktopItemLayoutEngine.ScaleIconSpacing(76, 96),
            DesktopItemLayoutEngine.ScaleIconSpacing(80, 96), 0);
        Assert.Equal(146, result.Items[0].Width);
        Assert.Equal(134, result.Items[0].Height);
        Assert.Equal(result.Items[0].Y, result.Items[1].Y);
        Assert.Equal(result.Items[0].Y + 134, result.Items[2].Y);
    }

    [Fact]
    public void NarrowViewportStillClipsCellsToItsWidth()
    {
        var result = DesktopItemLayoutEngine.Calculate(BoxViewMode.Grid,
            new LayoutRect(0, 0, 60, 200), 1, 96, 130, 134, 0);
        Assert.Equal(60, result.Items[0].Width);
    }
}
