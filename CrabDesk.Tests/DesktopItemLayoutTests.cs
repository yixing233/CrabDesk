using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class DesktopItemLayoutTests
{
    [Theory]
    [InlineData(BoxViewMode.Grid, 42, 82, 180)]
    [InlineData(BoxViewMode.Grid, 96, 82, 276)]
    [InlineData(BoxViewMode.Grid, 42, 160, 336)]
    [InlineData(BoxViewMode.List, 96, 160, 180)]
    public void MinimumBoxWidthAlwaysFitsTwoGridCells(
        BoxViewMode viewMode,
        double iconSize,
        double horizontalSpacing,
        double expected)
    {
        Assert.Equal(expected, DesktopItemLayoutEngine.GetMinimumBoxWidth(
            viewMode,
            iconSize,
            horizontalSpacing));
    }

    [Fact]
    public void GridLayoutUsesConfiguredSpacingAndStableColumns()
    {
        var result = DesktopItemLayoutEngine.Calculate(
            BoxViewMode.Grid,
            new LayoutRect(10, 20, 246, 200),
            5,
            42,
            82,
            88,
            0);

        Assert.Equal(5, result.Items.Count);
        Assert.Equal(new LayoutRect(10, 20, 82, 88), result.Items[0]);
        Assert.Equal(new LayoutRect(174, 20, 82, 88), result.Items[2]);
        Assert.Equal(new LayoutRect(10, 108, 82, 88), result.Items[3]);
        Assert.Equal(0, result.MaxScroll);
    }

    [Fact]
    public void ListLayoutUsesFullBodyWidth()
    {
        var result = DesktopItemLayoutEngine.Calculate(
            BoxViewMode.List,
            new LayoutRect(8, 46, 320, 140),
            3,
            42,
            82,
            88,
            0);

        Assert.Equal(new LayoutRect(8, 46, 320, 54), result.Items[0]);
        Assert.Equal(new LayoutRect(8, 154, 320, 54), result.Items[2]);
        Assert.Equal(22, result.MaxScroll);
    }

    [Fact]
    public void RequestedScrollIsClampedToAvailableContent()
    {
        var result = DesktopItemLayoutEngine.Calculate(
            BoxViewMode.List,
            new LayoutRect(0, 0, 300, 100),
            4,
            42,
            82,
            88,
            999);

        Assert.Equal(116, result.MaxScroll);
        Assert.Equal(116, result.ScrollOffset);
        Assert.Equal(-116, result.Items[0].Y);
    }

    [Fact]
    public void EmptyLayoutHasNoScrollOrItems()
    {
        var result = DesktopItemLayoutEngine.Calculate(
            BoxViewMode.Grid,
            new LayoutRect(0, 0, 300, 200),
            0,
            42,
            82,
            88,
            40);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.ScrollOffset);
        Assert.Equal(0, result.MaxScroll);
    }

    [Fact]
    public void SelectionIntersectionIncludesOverlappingItemsButNotTouchingEdges()
    {
        var selection = new LayoutRect(40, 40, 80, 60);

        Assert.True(selection.Intersects(new LayoutRect(20, 20, 30, 30)));
        Assert.True(selection.Intersects(new LayoutRect(100, 70, 40, 40)));
        Assert.False(selection.Intersects(new LayoutRect(120, 40, 20, 20)));
        Assert.False(selection.Intersects(new LayoutRect(60, 100, 20, 20)));
        Assert.False(selection.Intersects(new LayoutRect(50, 50, 0, 20)));
    }
}
