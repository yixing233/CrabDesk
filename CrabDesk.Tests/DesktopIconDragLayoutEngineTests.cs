using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class DesktopIconDragLayoutEngineTests
{
    [Fact]
    public void MovingToAnEmptyCellKeepsTheDraggedIconAtTheRequestedCell()
    {
        var result = DesktopIconDragLayoutEngine.Calculate(
            Items(("A", 0, 0), ("B", 0, 1), ("C", 0, 2)),
            ["A"],
            "A",
            new DesktopIconGridCell(0, 4),
            columnCount: 1,
            rowCount: 6);

        Assert.True(result.IsValid);
        Assert.Equal(new DesktopIconGridCell(0, 4), result.DraggedPlacements["A"]);
        Assert.Equal(new DesktopIconGridCell(0, 4), result.Placements["A"]);
        Assert.Equal(new DesktopIconGridCell(0, 1), result.Placements["B"]);
    }

    [Fact]
    public void MovingBetweenIconsShiftsTheOccupiedRunForward()
    {
        var result = DesktopIconDragLayoutEngine.Calculate(
            Items(("A", 0, 0), ("B", 0, 1), ("C", 0, 2), ("D", 0, 3)),
            ["A"],
            "A",
            new DesktopIconGridCell(0, 2),
            columnCount: 1,
            rowCount: 6);

        Assert.True(result.IsValid);
        Assert.Equal(new DesktopIconGridCell(0, 2), result.Placements["A"]);
        Assert.Equal(new DesktopIconGridCell(0, 1), result.Placements["B"]);
        Assert.Equal(new DesktopIconGridCell(0, 3), result.Placements["C"]);
        Assert.Equal(new DesktopIconGridCell(0, 4), result.Placements["D"]);
    }

    [Fact]
    public void MultiSelectMovesAsAFormationAndShiftsOnlyConflictingIcons()
    {
        var result = DesktopIconDragLayoutEngine.Calculate(
            Items(("A", 0, 0), ("B", 0, 1), ("C", 0, 2), ("D", 0, 3), ("E", 0, 4)),
            ["A", "B"],
            "A",
            new DesktopIconGridCell(0, 2),
            columnCount: 1,
            rowCount: 8);

        Assert.True(result.IsValid);
        Assert.Equal(new DesktopIconGridCell(0, 2), result.Placements["A"]);
        Assert.Equal(new DesktopIconGridCell(0, 3), result.Placements["B"]);
        Assert.Equal(new DesktopIconGridCell(0, 4), result.Placements["C"]);
        Assert.Equal(new DesktopIconGridCell(0, 5), result.Placements["D"]);
        Assert.Equal(new DesktopIconGridCell(0, 6), result.Placements["E"]);
    }

    [Fact]
    public void BlockedCellsAreSkippedWhenResolvingTheDraggedFormation()
    {
        var result = DesktopIconDragLayoutEngine.Calculate(
            Items(("A", 0, 0), ("B", 0, 1), ("C", 0, 3)),
            ["A"],
            "A",
            new DesktopIconGridCell(0, 1),
            columnCount: 1,
            rowCount: 6,
            blockedCells: [new DesktopIconGridCell(0, 1)]);

        Assert.True(result.IsValid);
        Assert.NotEqual(new DesktopIconGridCell(0, 1), result.DraggedPlacements["A"]);
        Assert.InRange(result.DraggedPlacements["A"].Row, 0, 5);
    }

    [Fact]
    public void BoxItemsInsertAtTheRequestedDesktopCellAndShiftExistingItems()
    {
        var result = DesktopIconDragLayoutEngine.CalculateInsertion(
            Items(("A", 0, 0), ("B", 0, 1), ("C", 0, 2)),
            ["X"],
            new DesktopIconGridCell(0, 1),
            columnCount: 1,
            rowCount: 6);

        Assert.True(result.IsValid);
        Assert.Equal(new DesktopIconGridCell(0, 1), result.DraggedPlacements["X"]);
        Assert.Equal(new DesktopIconGridCell(0, 2), result.Placements["B"]);
        Assert.Equal(new DesktopIconGridCell(0, 3), result.Placements["C"]);
    }

    [Fact]
    public void MultipleBoxItemsKeepTheirFormationDuringInsertion()
    {
        var result = DesktopIconDragLayoutEngine.CalculateInsertion(
            Items(("A", 0, 0), ("B", 0, 1)),
            ["X", "Y"],
            new DesktopIconGridCell(0, 1),
            columnCount: 1,
            rowCount: 6);

        Assert.True(result.IsValid);
        Assert.Equal(new DesktopIconGridCell(0, 1), result.DraggedPlacements["X"]);
        Assert.Equal(new DesktopIconGridCell(0, 2), result.DraggedPlacements["Y"]);
        Assert.Equal(new DesktopIconGridCell(0, 0), result.Placements["A"]);
        Assert.Equal(new DesktopIconGridCell(0, 3), result.Placements["B"]);
    }

    private static IReadOnlyList<DesktopIconGridItem> Items(
        params (string Key, int Column, int Row)[] items) =>
        items.Select(item => new DesktopIconGridItem(
            item.Key,
            new DesktopIconGridCell(item.Column, item.Row))).ToArray();
}
