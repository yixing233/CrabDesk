namespace CrabDesk.Core;

/// <summary>
/// A logical desktop grid coordinate. The desktop surface converts this to
/// monitor-relative pixels only while drawing.
/// </summary>
public readonly record struct DesktopIconGridCell(int Column, int Row);

/// <summary>
/// An item currently occupying a desktop grid coordinate.
/// </summary>
public sealed record DesktopIconGridItem(string Key, DesktopIconGridCell Cell);

/// <summary>
/// The complete preview for one manual desktop-icon drag. <see cref="Placements"/>
/// contains both moved icons and any icons shifted out of their way.
/// </summary>
public sealed record DesktopIconDragLayoutResult(
    IReadOnlyDictionary<string, DesktopIconGridCell> Placements,
    IReadOnlyDictionary<string, DesktopIconGridCell> DraggedPlacements,
    DesktopIconGridCell? ResolvedAnchor)
{
    public bool IsValid => ResolvedAnchor is not null && DraggedPlacements.Count > 0;
}

/// <summary>
/// Calculates the temporary layout shown while desktop icons are dragged.
/// A drop on an occupied cell inserts the selected icon group and shifts the
/// intervening icons forward through the desktop's column-major grid.
/// </summary>
public static class DesktopIconDragLayoutEngine
{
    /// <summary>
    /// Calculates an insertion for items that currently have no desktop cell,
    /// such as icons being released from a CrabDesk box. The incoming items are
    /// treated as a compact column-major formation anchored at the requested
    /// cell; existing desktop items are shifted using the same rules as a
    /// normal desktop drag.
    /// </summary>
    public static DesktopIconDragLayoutResult CalculateInsertion(
        IEnumerable<DesktopIconGridItem> stationaryItems,
        IEnumerable<string> movingKeys,
        DesktopIconGridCell requestedAnchor,
        int columnCount,
        int rowCount,
        IEnumerable<DesktopIconGridCell>? blockedCells = null)
    {
        ArgumentNullException.ThrowIfNull(stationaryItems);
        ArgumentNullException.ThrowIfNull(movingKeys);

        var keys = movingKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (keys.Length == 0)
        {
            var placements = stationaryItems.ToDictionary(
                item => item.Key,
                item => item.Cell,
                StringComparer.OrdinalIgnoreCase);
            return new DesktopIconDragLayoutResult(
                placements,
                new Dictionary<string, DesktopIconGridCell>(StringComparer.OrdinalIgnoreCase),
                null);
        }

        rowCount = Math.Max(1, rowCount);
        var syntheticMoving = keys.Select((key, index) => new DesktopIconGridItem(
            key,
            new DesktopIconGridCell(index / rowCount, index % rowCount)));
        return Calculate(
            stationaryItems.Concat(syntheticMoving),
            keys,
            keys[0],
            requestedAnchor,
            columnCount,
            rowCount,
            blockedCells);
    }

    public static DesktopIconDragLayoutResult Calculate(
        IEnumerable<DesktopIconGridItem> items,
        IEnumerable<string> movingKeys,
        string anchorKey,
        DesktopIconGridCell requestedAnchor,
        int columnCount,
        int rowCount,
        IEnumerable<DesktopIconGridCell>? blockedCells = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(movingKeys);

        columnCount = Math.Max(0, columnCount);
        rowCount = Math.Max(0, rowCount);
        var source = items
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var sourcePlacements = source.ToDictionary(
            item => item.Key,
            item => item.Cell,
            StringComparer.OrdinalIgnoreCase);
        var emptyDraggedPlacements = new Dictionary<string, DesktopIconGridCell>(StringComparer.OrdinalIgnoreCase);
        if (columnCount == 0 || rowCount == 0 ||
            string.IsNullOrWhiteSpace(anchorKey) ||
            !sourcePlacements.TryGetValue(anchorKey, out var anchorSourceCell))
        {
            return new DesktopIconDragLayoutResult(sourcePlacements, emptyDraggedPlacements, null);
        }

        var movingKeySet = movingKeys
            .Where(key => !string.IsNullOrWhiteSpace(key) && sourcePlacements.ContainsKey(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!movingKeySet.Contains(anchorKey))
        {
            return new DesktopIconDragLayoutResult(sourcePlacements, emptyDraggedPlacements, null);
        }

        var moving = source
            .Where(item => movingKeySet.Contains(item.Key))
            .OrderBy(item => GetGridOrder(item.Cell, rowCount))
            .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (moving.Length == 0)
        {
            return new DesktopIconDragLayoutResult(sourcePlacements, emptyDraggedPlacements, null);
        }

        var blocked = (blockedCells ?? [])
            .Where(cell => IsInBounds(cell, columnCount, rowCount))
            .ToHashSet();
        var resolvedAnchor = ResolveAnchor(
            requestedAnchor,
            anchorSourceCell,
            moving,
            blocked,
            columnCount,
            rowCount);
        if (resolvedAnchor is not { } anchorCell)
        {
            return new DesktopIconDragLayoutResult(sourcePlacements, emptyDraggedPlacements, null);
        }

        var draggedPlacements = moving.ToDictionary(
            item => item.Key,
            item => new DesktopIconGridCell(
                anchorCell.Column + item.Cell.Column - anchorSourceCell.Column,
                anchorCell.Row + item.Cell.Row - anchorSourceCell.Row),
            StringComparer.OrdinalIgnoreCase);
        var previewPlacements = new Dictionary<string, DesktopIconGridCell>(sourcePlacements, StringComparer.OrdinalIgnoreCase);
        var occupied = source
            .Where(item => !movingKeySet.Contains(item.Key))
            .Where(item => IsInBounds(item.Cell, columnCount, rowCount))
            .GroupBy(item => item.Cell)
            .ToDictionary(group => group.Key, group => group.First().Key);
        var reservedTargets = draggedPlacements.Values.ToHashSet();

        // Process target runs in visual order. A contiguous occupied run is
        // shifted once as a block, so a multi-select does not move the same
        // icon twice when two adjacent target cells are reserved.
        foreach (var target in reservedTargets.OrderBy(cell => GetGridOrder(cell, rowCount)))
        {
            if (occupied.ContainsKey(target) &&
                !TryShiftRunAtTarget(
                    target,
                    blocked,
                    reservedTargets,
                    occupied,
                    previewPlacements,
                    columnCount,
                    rowCount))
            {
                return new DesktopIconDragLayoutResult(sourcePlacements, emptyDraggedPlacements, null);
            }
        }

        foreach (var (key, target) in draggedPlacements)
        {
            occupied[target] = key;
            previewPlacements[key] = target;
        }

        return new DesktopIconDragLayoutResult(previewPlacements, draggedPlacements, anchorCell);
    }

    private static DesktopIconGridCell? ResolveAnchor(
        DesktopIconGridCell requested,
        DesktopIconGridCell sourceAnchor,
        IReadOnlyList<DesktopIconGridItem> moving,
        IReadOnlySet<DesktopIconGridCell> blocked,
        int columnCount,
        int rowCount)
    {
        var clamped = new DesktopIconGridCell(
            Math.Clamp(requested.Column, 0, columnCount - 1),
            Math.Clamp(requested.Row, 0, rowCount - 1));
        foreach (var candidate in EnumerateCells(columnCount, rowCount)
                     .OrderBy(cell => Math.Abs(cell.Column - clamped.Column) + Math.Abs(cell.Row - clamped.Row))
                     .ThenBy(cell => GetGridOrder(cell, rowCount)))
        {
            var targets = moving
                .Select(item => new DesktopIconGridCell(
                    candidate.Column + item.Cell.Column - sourceAnchor.Column,
                    candidate.Row + item.Cell.Row - sourceAnchor.Row))
                .ToArray();
            if (targets.All(cell => IsInBounds(cell, columnCount, rowCount) && !blocked.Contains(cell)) &&
                targets.Distinct().Count() == targets.Length)
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool TryShiftRunAtTarget(
        DesktopIconGridCell target,
        IReadOnlySet<DesktopIconGridCell> blocked,
        IReadOnlySet<DesktopIconGridCell> reservedTargets,
        IDictionary<DesktopIconGridCell, string> occupied,
        IDictionary<string, DesktopIconGridCell> placements,
        int columnCount,
        int rowCount)
    {
        var cells = EnumerateCells(columnCount, rowCount).ToArray();
        var start = GetGridOrder(target, rowCount);
        var keys = new List<string>();
        for (var index = start; index < cells.Length; index++)
        {
            var cell = cells[index];
            if (blocked.Contains(cell))
            {
                continue;
            }

            if (occupied.TryGetValue(cell, out var key))
            {
                keys.Add(key);
                continue;
            }

            if (reservedTargets.Contains(cell))
            {
                continue;
            }

            break;
        }

        if (keys.Count == 0)
        {
            return true;
        }

        var output = cells
            .Skip(start)
            .Where(cell => !blocked.Contains(cell) && !reservedTargets.Contains(cell))
            .Take(keys.Count)
            .ToArray();
        if (output.Length != keys.Count)
        {
            return false;
        }

        foreach (var key in keys)
        {
            var source = occupied.FirstOrDefault(entry =>
                string.Equals(entry.Value, key, StringComparison.OrdinalIgnoreCase)).Key;
            occupied.Remove(source);
        }

        for (var index = 0; index < keys.Count; index++)
        {
            occupied[output[index]] = keys[index];
            placements[keys[index]] = output[index];
        }

        return true;
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
        cell.Column >= 0 && cell.Column < columnCount && cell.Row >= 0 && cell.Row < rowCount;

    private static int GetGridOrder(DesktopIconGridCell cell, int rowCount) =>
        cell.Column * rowCount + cell.Row;
}
