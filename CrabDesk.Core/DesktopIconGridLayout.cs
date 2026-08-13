namespace CrabDesk.Core;

/// <summary>
/// The usable cell metrics for the replacement desktop icon surface.
/// </summary>
public readonly record struct DesktopIconGridMetrics(
    double HorizontalSpacing,
    double VerticalSpacing,
    int ColumnCount,
    int RowCount);

public static class DesktopIconGridLayout
{
    /// <summary>
    /// Builds a grid that uses the entire available desktop work area. The
    /// native horizontal cadence is retained, while the final partial
    /// vertical slot is included and the vertical step is fitted to the
    /// usable height. This avoids leaving an almost full icon row above the
    /// taskbar.
    /// </summary>
    public static DesktopIconGridMetrics CalculateSurfaceMetrics(
        double width,
        double height,
        double nativeHorizontalSpacing,
        double nativeVerticalSpacing)
    {
        width = NormalizeExtent(width);
        height = NormalizeExtent(height);
        nativeHorizontalSpacing = NormalizeSpacing(nativeHorizontalSpacing);
        nativeVerticalSpacing = NormalizeSpacing(nativeVerticalSpacing);

        var columnCount = (int)Math.Floor(width / nativeHorizontalSpacing);
        var rowCount = (int)Math.Ceiling(height / nativeVerticalSpacing);
        var verticalSpacing = rowCount > 0
            ? height / rowCount
            : nativeVerticalSpacing;
        return new DesktopIconGridMetrics(
            nativeHorizontalSpacing,
            verticalSpacing,
            columnCount,
            rowCount);
    }

    public static IReadOnlyList<DesktopIconPositionSnapshot> Align(
        IEnumerable<DesktopIconPositionSnapshot> positions,
        int horizontalSpacing,
        int verticalSpacing)
    {
        var source = positions.ToArray();
        if (source.Length == 0)
        {
            return [];
        }

        horizontalSpacing = Math.Max(1, horizontalSpacing);
        verticalSpacing = Math.Max(1, verticalSpacing);
        var originX = ResolveGridOrigin(source.Select(position => position.X), horizontalSpacing);
        var originY = ResolveGridOrigin(source.Select(position => position.Y), verticalSpacing);
        var result = new DesktopIconPositionSnapshot[source.Length];
        var occupied = new HashSet<(int Column, int Row)>();
        foreach (var entry in source
                     .Select((position, index) => (Position: position, Index: index))
                     .OrderBy(entry => entry.Position.Y)
                     .ThenBy(entry => entry.Position.X)
                     .ThenBy(entry => entry.Position.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var column = Math.Max(0, (int)Math.Round(
                (entry.Position.X - originX) / (double)horizontalSpacing,
                MidpointRounding.AwayFromZero));
            var row = Math.Max(0, (int)Math.Round(
                (entry.Position.Y - originY) / (double)verticalSpacing,
                MidpointRounding.AwayFromZero));
            var cell = FindNearestFreeCell(column, row, occupied);
            occupied.Add(cell);
            result[entry.Index] = entry.Position with
            {
                X = originX + cell.Column * horizontalSpacing,
                Y = originY + cell.Row * verticalSpacing
            };
        }
        return result;
    }

    /// <summary>
    /// Refills a desktop grid after its capacity changes without re-sorting the
    /// user's manually arranged icons. Explorer reads desktop cells down each
    /// column before moving to the next column, so that same order is retained
    /// while the new grid is filled.
    /// </summary>
    public static IReadOnlyDictionary<string, DesktopIconGridCell> Reflow(
        IEnumerable<DesktopIconGridItem> items,
        int columnCount,
        int rowCount,
        IEnumerable<DesktopIconGridCell>? blockedCells = null)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (columnCount <= 0 || rowCount <= 0)
        {
            return new Dictionary<string, DesktopIconGridCell>(StringComparer.OrdinalIgnoreCase);
        }

        var orderedItems = items
            .Select((item, index) => (Item: item, Index: index))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Item.Key))
            .GroupBy(entry => entry.Item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(entry => entry.Item.Cell.Column)
            .ThenBy(entry => entry.Item.Cell.Row)
            .ThenBy(entry => entry.Index)
            .ThenBy(entry => entry.Item.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var blocked = (blockedCells ?? [])
            .Where(cell => IsInBounds(cell, columnCount, rowCount))
            .ToHashSet();
        var availableCells = EnumerateCells(columnCount, rowCount)
            .Where(cell => !blocked.Contains(cell))
            .ToArray();
        var result = new Dictionary<string, DesktopIconGridCell>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < orderedItems.Length && index < availableCells.Length; index++)
        {
            result[orderedItems[index].Item.Key] = availableCells[index];
        }

        return result;
    }

    private static double NormalizeExtent(double value) =>
        double.IsFinite(value) ? Math.Max(0, value) : 0;

    private static double NormalizeSpacing(double value) =>
        double.IsFinite(value) ? Math.Max(1, value) : 1;

    private static int ResolveGridOrigin(IEnumerable<int> coordinates, int spacing) => coordinates
        .Select(coordinate => PositiveRemainder(coordinate, spacing))
        .GroupBy(remainder => remainder)
        .OrderByDescending(group => group.Count())
        .ThenBy(group => group.Key)
        .First()
        .Key;

    private static (int Column, int Row) FindNearestFreeCell(
        int column,
        int row,
        IReadOnlySet<(int Column, int Row)> occupied)
    {
        if (!occupied.Contains((column, row)))
        {
            return (column, row);
        }

        for (var distance = 1; ; distance++)
        {
            for (var rowOffset = 0; rowOffset <= distance; rowOffset++)
            {
                var columnOffset = distance - rowOffset;
                foreach (var candidate in EnumerateCandidates(column, row, columnOffset, rowOffset))
                {
                    if (candidate.Column >= 0 && candidate.Row >= 0 && !occupied.Contains(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }
    }

    private static IEnumerable<(int Column, int Row)> EnumerateCandidates(
        int column,
        int row,
        int columnOffset,
        int rowOffset)
    {
        yield return (column + columnOffset, row + rowOffset);
        if (columnOffset != 0) yield return (column - columnOffset, row + rowOffset);
        if (rowOffset != 0) yield return (column + columnOffset, row - rowOffset);
        if (columnOffset != 0 && rowOffset != 0) yield return (column - columnOffset, row - rowOffset);
    }

    private static IEnumerable<DesktopIconGridCell> EnumerateCells(int columnCount, int rowCount)
    {
        for (var column = 0; column < columnCount; column++)
        {
            for (var row = 0; row < rowCount; row++)
            {
                yield return new DesktopIconGridCell(column, row);
            }
        }
    }

    private static bool IsInBounds(DesktopIconGridCell cell, int columnCount, int rowCount) =>
        cell.Column >= 0 && cell.Column < columnCount &&
        cell.Row >= 0 && cell.Row < rowCount;

    private static int PositiveRemainder(int value, int divisor) => ((value % divisor) + divisor) % divisor;
}
