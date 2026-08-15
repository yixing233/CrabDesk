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

    [Fact]
    public void MultiSelectOntoAdjacentOccupiedCellsCompletesTheCascade()
    {
        // Both reserved targets are occupied; the cascade must still shift the
        // tail once and place the overflow into the vacated source cells
        // instead of failing the whole drop.
        var result = DesktopIconDragLayoutEngine.Calculate(
            Items(("A", 0, 0), ("B", 0, 1), ("C", 0, 2), ("D", 0, 3), ("E", 0, 4)),
            ["A", "B"],
            "A",
            new DesktopIconGridCell(0, 2),
            columnCount: 1,
            rowCount: 6);

        Assert.True(result.IsValid);
        Assert.Equal(new DesktopIconGridCell(0, 2), result.Placements["A"]);
        Assert.Equal(new DesktopIconGridCell(0, 3), result.Placements["B"]);
        Assert.Equal(new DesktopIconGridCell(0, 4), result.Placements["C"]);
        Assert.Equal(new DesktopIconGridCell(0, 5), result.Placements["D"]);
        Assert.Equal(new DesktopIconGridCell(0, 0), result.Placements["E"]);
    }

    [Fact]
    public void DropInAFullGridStillCompletesUsingTheVacatedCell()
    {
        var result = DesktopIconDragLayoutEngine.Calculate(
            Items(("A", 0, 0), ("B", 0, 1), ("C", 0, 2)),
            ["A"],
            "A",
            new DesktopIconGridCell(0, 2),
            columnCount: 1,
            rowCount: 3);

        Assert.True(result.IsValid);
        Assert.Equal(new DesktopIconGridCell(0, 2), result.Placements["A"]);
        Assert.Equal(new DesktopIconGridCell(0, 1), result.Placements["B"]);
        Assert.Equal(new DesktopIconGridCell(0, 0), result.Placements["C"]);
    }

    [Fact]
    public void MultiSelectIntoAnEmptyGapDoesNotShiftAnything()
    {
        var result = DesktopIconDragLayoutEngine.Calculate(
            Items(("A", 0, 0), ("B", 0, 1), ("C", 0, 3), ("D", 0, 4)),
            ["A", "B"],
            "A",
            new DesktopIconGridCell(0, 2),
            columnCount: 1,
            rowCount: 6);

        Assert.True(result.IsValid);
        Assert.Equal(new DesktopIconGridCell(0, 2), result.Placements["A"]);
        // B lands on C's cell, so C shifts forward and D follows.
        Assert.Equal(new DesktopIconGridCell(0, 3), result.Placements["B"]);
        Assert.Equal(new DesktopIconGridCell(0, 4), result.Placements["C"]);
        Assert.Equal(new DesktopIconGridCell(0, 5), result.Placements["D"]);
    }

    [Fact]
    public void CrossColumnDropShiftsTheTailInColumnMajorOrder()
    {
        var result = DesktopIconDragLayoutEngine.Calculate(
            Items(("A", 0, 0), ("B", 0, 1), ("C", 0, 2), ("D", 1, 0), ("E", 1, 1)),
            ["A"],
            "A",
            new DesktopIconGridCell(0, 2),
            columnCount: 2,
            rowCount: 3);

        Assert.True(result.IsValid);
        Assert.Equal(new DesktopIconGridCell(0, 2), result.Placements["A"]);
        Assert.Equal(new DesktopIconGridCell(0, 1), result.Placements["B"]);
        Assert.Equal(new DesktopIconGridCell(1, 0), result.Placements["C"]);
        Assert.Equal(new DesktopIconGridCell(1, 1), result.Placements["D"]);
        Assert.Equal(new DesktopIconGridCell(1, 2), result.Placements["E"]);
    }

    [Fact]
    public void RightSqueezePushesTheRowSidewaysThenCascadesDown()
    {
        var result = DesktopIconDragLayoutEngine.Calculate(
            Items(("A", 0, 0), ("B", 1, 0), ("C", 2, 0), ("X", 3, 0),
                  ("D", 0, 1), ("E", 1, 1), ("F", 2, 1), ("G", 3, 1)),
            ["A"],
            "A",
            new DesktopIconGridCell(2, 0),
            columnCount: 4,
            rowCount: 2,
            direction: DesktopIconSqueezeDirection.Right);

        Assert.True(result.IsValid);
        Assert.Equal(new DesktopIconGridCell(2, 0), result.Placements["A"]);
        Assert.Equal(new DesktopIconGridCell(1, 0), result.Placements["B"]);
        Assert.Equal(new DesktopIconGridCell(3, 0), result.Placements["C"]);
        Assert.Equal(new DesktopIconGridCell(3, 1), result.Placements["X"]);
        Assert.Equal(new DesktopIconGridCell(0, 1), result.Placements["D"]);
        Assert.Equal(new DesktopIconGridCell(1, 1), result.Placements["E"]);
        Assert.Equal(new DesktopIconGridCell(2, 1), result.Placements["F"]);
        Assert.Equal(new DesktopIconGridCell(0, 0), result.Placements["G"]);
    }

    [Fact]
    public void ReplacingAnIconPushesTheRowToTheRightInsteadOfShiftingTheNeighbour()
    {
        // The reported case: dragging C onto B's cell must yield ACBX, not
        // B C X with A squeezed away.
        var result = DesktopIconDragLayoutEngine.Calculate(
            Items(("A", 0, 0), ("B", 1, 0), ("C", 2, 0), ("X", 3, 0),
                  ("D", 0, 1), ("E", 1, 1), ("F", 2, 1)),
            ["C"],
            "C",
            new DesktopIconGridCell(1, 0),
            columnCount: 4,
            rowCount: 2,
            direction: DesktopIconSqueezeDirection.Right);

        Assert.True(result.IsValid);
        Assert.Equal(new DesktopIconGridCell(0, 0), result.Placements["A"]);
        Assert.Equal(new DesktopIconGridCell(1, 0), result.Placements["C"]);
        Assert.Equal(new DesktopIconGridCell(2, 0), result.Placements["B"]);
        Assert.Equal(new DesktopIconGridCell(3, 0), result.Placements["X"]);
        Assert.Equal(new DesktopIconGridCell(0, 1), result.Placements["D"]);
        Assert.Equal(new DesktopIconGridCell(1, 1), result.Placements["E"]);
        Assert.Equal(new DesktopIconGridCell(2, 1), result.Placements["F"]);
    }

    [Fact]
    public void SqueezeWithAnEmptyGapInTheRowDoesNotMoveIconsBeyondTheGap()
    {
        var result = DesktopIconDragLayoutEngine.Calculate(
            Items(("A", 0, 0), ("B", 1, 0), ("D", 0, 1), ("E", 1, 1), ("F", 2, 1)),
            ["A"],
            "A",
            new DesktopIconGridCell(2, 0),
            columnCount: 3,
            rowCount: 2,
            direction: DesktopIconSqueezeDirection.Right);

        Assert.True(result.IsValid);
        // (2,0) is the gap: A lands there and nothing else moves.
        Assert.Equal(new DesktopIconGridCell(2, 0), result.Placements["A"]);
        Assert.Equal(new DesktopIconGridCell(1, 0), result.Placements["B"]);
        Assert.Equal(new DesktopIconGridCell(0, 1), result.Placements["D"]);
        Assert.Equal(new DesktopIconGridCell(1, 1), result.Placements["E"]);
        Assert.Equal(new DesktopIconGridCell(2, 1), result.Placements["F"]);
    }

    [Fact]
    public void DroppingOntoAnOrthogonalNeighbourSwapsTheTwoIcons()
    {
        var result = DesktopIconDragLayoutEngine.Calculate(
            Items(("A", 0, 0), ("B", 1, 0), ("C", 2, 0)),
            ["A"],
            "A",
            new DesktopIconGridCell(1, 0),
            columnCount: 3,
            rowCount: 3);

        Assert.True(result.IsValid);
        Assert.Equal(new DesktopIconGridCell(1, 0), result.Placements["A"]);
        Assert.Equal(new DesktopIconGridCell(0, 0), result.Placements["B"]);
        Assert.Equal(new DesktopIconGridCell(2, 0), result.Placements["C"]);
    }

    [Fact]
    public void DroppingOntoAVerticalNeighbourSwapsTheTwoIcons()
    {
        var result = DesktopIconDragLayoutEngine.Calculate(
            Items(("A", 0, 0), ("B", 0, 1), ("C", 0, 2)),
            ["A"],
            "A",
            new DesktopIconGridCell(0, 1),
            columnCount: 1,
            rowCount: 3);

        Assert.True(result.IsValid);
        Assert.Equal(new DesktopIconGridCell(0, 1), result.Placements["A"]);
        Assert.Equal(new DesktopIconGridCell(0, 0), result.Placements["B"]);
        Assert.Equal(new DesktopIconGridCell(0, 2), result.Placements["C"]);
    }

    [Fact]
    public void DroppingOntoAnEmptyNeighbourJustMovesTheIcon()
    {
        var result = DesktopIconDragLayoutEngine.Calculate(
            Items(("A", 0, 0), ("C", 2, 0)),
            ["A"],
            "A",
            new DesktopIconGridCell(1, 0),
            columnCount: 3,
            rowCount: 3);

        Assert.True(result.IsValid);
        Assert.Equal(new DesktopIconGridCell(1, 0), result.Placements["A"]);
        Assert.Equal(new DesktopIconGridCell(2, 0), result.Placements["C"]);
    }

    [Fact]
    public void DistantOrDiagonalDropsStillInsertInsteadOfSwapping()
    {
        // Two cells away: insertion (column-major cascade), not a swap.
        var result = DesktopIconDragLayoutEngine.Calculate(
            Items(("A", 0, 0), ("B", 1, 0), ("C", 2, 0)),
            ["A"],
            "A",
            new DesktopIconGridCell(2, 0),
            columnCount: 3,
            rowCount: 3,
            direction: DesktopIconSqueezeDirection.Right);

        Assert.True(result.IsValid);
        Assert.Equal(new DesktopIconGridCell(2, 0), result.Placements["A"]);
        Assert.Equal(new DesktopIconGridCell(1, 0), result.Placements["B"]);
        Assert.Equal(new DesktopIconGridCell(2, 1), result.Placements["C"]);
    }

    [Fact]
    public void MultiSelectDoesNotSwapEvenWhenAdjacent()
    {
        var result = DesktopIconDragLayoutEngine.Calculate(
            Items(("A", 0, 0), ("B", 0, 1), ("C", 1, 0), ("D", 1, 1)),
            ["A", "B"],
            "A",
            new DesktopIconGridCell(1, 0),
            columnCount: 2,
            rowCount: 2);

        Assert.True(result.IsValid);
        Assert.Equal(new DesktopIconGridCell(1, 0), result.Placements["A"]);
        Assert.Equal(new DesktopIconGridCell(1, 1), result.Placements["B"]);
        Assert.Equal(new DesktopIconGridCell(0, 0), result.Placements["C"]);
        Assert.Equal(new DesktopIconGridCell(0, 1), result.Placements["D"]);
    }

    private static IReadOnlyList<DesktopIconGridItem> Items(
        params (string Key, int Column, int Row)[] items) =>
        items.Select(item => new DesktopIconGridItem(
            item.Key,
            new DesktopIconGridCell(item.Column, item.Row))).ToArray();
}
