using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class DesktopIconGridLayoutTests
{
    [Fact]
    public void CalculateSurfaceMetricsFillsTheFinalPartialVerticalSlot()
    {
        var metrics = DesktopIconGridLayout.CalculateSurfaceMetrics(
            width: 1690.667,
            height: 1010.667,
            nativeHorizontalSpacing: 64,
            nativeVerticalSpacing: 92);

        Assert.Equal(26, metrics.ColumnCount);
        Assert.Equal(11, metrics.RowCount);
        Assert.Equal(64, metrics.HorizontalSpacing, 3);
        Assert.InRange(metrics.VerticalSpacing, 91, 92);
        Assert.Equal(1010.667, metrics.VerticalSpacing * metrics.RowCount, 3);
    }

    [Fact]
    public void CalculateSurfaceMetricsPreservesAnEvenNativeVerticalStep()
    {
        var metrics = DesktopIconGridLayout.CalculateSurfaceMetrics(
            width: 640,
            height: 920,
            nativeHorizontalSpacing: 64,
            nativeVerticalSpacing: 92);

        Assert.Equal(10, metrics.ColumnCount);
        Assert.Equal(10, metrics.RowCount);
        Assert.Equal(64, metrics.HorizontalSpacing, 3);
        Assert.Equal(92, metrics.VerticalSpacing, 3);
    }

    [Fact]
    public void ReflowUsesColumnMajorManualOrderWhenZoomAddsRows()
    {
        var reflowed = DesktopIconGridLayout.Reflow(
            Items(("A", 0, 0), ("B", 0, 1), ("C", 1, 0), ("D", 1, 1)),
            columnCount: 2,
            rowCount: 3);

        Assert.Equal(new DesktopIconGridCell(0, 0), reflowed["A"]);
        Assert.Equal(new DesktopIconGridCell(0, 1), reflowed["B"]);
        Assert.Equal(new DesktopIconGridCell(0, 2), reflowed["C"]);
        Assert.Equal(new DesktopIconGridCell(1, 0), reflowed["D"]);
    }

    [Fact]
    public void ReflowUsesColumnMajorManualOrderWhenZoomRemovesRows()
    {
        var reflowed = DesktopIconGridLayout.Reflow(
            Items(("A", 0, 0), ("B", 0, 1), ("C", 0, 2), ("D", 1, 0), ("E", 1, 1)),
            columnCount: 3,
            rowCount: 2);

        Assert.Equal(new DesktopIconGridCell(0, 0), reflowed["A"]);
        Assert.Equal(new DesktopIconGridCell(0, 1), reflowed["B"]);
        Assert.Equal(new DesktopIconGridCell(1, 0), reflowed["C"]);
        Assert.Equal(new DesktopIconGridCell(1, 1), reflowed["D"]);
        Assert.Equal(new DesktopIconGridCell(2, 0), reflowed["E"]);
    }

    [Fact]
    public void ReflowSkipsBlockedCellsWithoutChangingManualOrder()
    {
        var reflowed = DesktopIconGridLayout.Reflow(
            Items(("A", 0, 0), ("B", 0, 1), ("C", 1, 0)),
            columnCount: 2,
            rowCount: 2,
            blockedCells: [new DesktopIconGridCell(0, 1)]);

        Assert.Equal(new DesktopIconGridCell(0, 0), reflowed["A"]);
        Assert.Equal(new DesktopIconGridCell(1, 0), reflowed["B"]);
        Assert.Equal(new DesktopIconGridCell(1, 1), reflowed["C"]);
    }

    [Fact]
    public void ReflowPreservesTheManualSequenceInsteadOfSortingNames()
    {
        var reflowed = DesktopIconGridLayout.Reflow(
            Items(("Z-last", 0, 0), ("A-first", 0, 1), ("M-middle", 1, 0)),
            columnCount: 2,
            rowCount: 2);

        Assert.Equal(new DesktopIconGridCell(0, 0), reflowed["Z-last"]);
        Assert.Equal(new DesktopIconGridCell(0, 1), reflowed["A-first"]);
        Assert.Equal(new DesktopIconGridCell(1, 0), reflowed["M-middle"]);
    }

    [Fact]
    public void AlignSnapsOutlierToDominantExplorerGrid()
    {
        var positions = new[]
        {
            new DesktopIconPositionSnapshot("A", 20, 2),
            new DesktopIconPositionSnapshot("B", 96, 87),
            new DesktopIconPositionSnapshot("C", 178, 174)
        };

        var aligned = DesktopIconGridLayout.Align(positions, 76, 85);

        Assert.Equal((20, 2), (aligned[0].X, aligned[0].Y));
        Assert.Equal((96, 87), (aligned[1].X, aligned[1].Y));
        Assert.Equal((172, 172), (aligned[2].X, aligned[2].Y));
    }

    [Fact]
    public void AlignMovesRoundedCollisionsToNearestFreeCells()
    {
        var positions = new[]
        {
            new DesktopIconPositionSnapshot("A", 20, 2),
            new DesktopIconPositionSnapshot("B", 30, 10)
        };

        var aligned = DesktopIconGridLayout.Align(positions, 76, 85);

        Assert.Equal(2, aligned.Select(position => (position.X, position.Y)).Distinct().Count());
        Assert.All(aligned, position =>
        {
            Assert.Equal(20, position.X % 76);
            Assert.Equal(2, position.Y % 85);
        });
    }

    private static IReadOnlyList<DesktopIconGridItem> Items(
        params (string Key, int Column, int Row)[] items) =>
        items.Select(item => new DesktopIconGridItem(
            item.Key,
            new DesktopIconGridCell(item.Column, item.Row))).ToArray();
}
