namespace CrabDesk.Core;

public static class DesktopIconGridLayout
{
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

    private static int PositiveRemainder(int value, int divisor) => ((value % divisor) + divisor) % divisor;
}
