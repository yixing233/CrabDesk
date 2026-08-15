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
/// How the displaced icons flow when a drop squeezes a row or column. The
/// drop point's position inside the target cell decides where the dragged
/// icon is inserted: dropping onto the left half of a cell (or into the gap
/// left of it) inserts at that cell and pushes the row to the right, while a
/// dead-center drop keeps the classic column-major downward cascade.
/// </summary>
public enum DesktopIconSqueezeDirection
{
    /// <summary>Classic column-major cascade (insert at the cell, push the tail down).</summary>
    Down,
    /// <summary>Row-wise insertion (insert at the cell, push the row to the right).</summary>
    Right
}

/// <summary>
/// Calculates the temporary layout shown while desktop icons are dragged.
/// A drop on an occupied cell inserts the selected icon group and shifts the
/// intervening icons forward through the desktop's grid.
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
        // Box releases synthesize a source formation at (0,0); the swap
        // shortcut is meaningless for them, so keep the insertion cascade.
        return Calculate(
            stationaryItems.Concat(syntheticMoving),
            keys,
            keys[0],
            requestedAnchor,
            columnCount,
            rowCount,
            blockedCells,
            allowSwap: false);
    }

    public static DesktopIconDragLayoutResult Calculate(
        IEnumerable<DesktopIconGridItem> items,
        IEnumerable<string> movingKeys,
        string anchorKey,
        DesktopIconGridCell requestedAnchor,
        int columnCount,
        int rowCount,
        IEnumerable<DesktopIconGridCell>? blockedCells = null,
        DesktopIconSqueezeDirection direction = DesktopIconSqueezeDirection.Down,
        bool allowSwap = true)
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

        // A single icon dropped onto one of its four orthogonal neighbours
        // swaps the two icons instead of inserting and squeezing the row or
        // column. Dropping onto an empty neighbour simply moves the icon
        // there (the swap path falls through to the normal placement).
        if (moving.Length == 1 && allowSwap)
        {
            var clampedRequested = new DesktopIconGridCell(
                Math.Clamp(requestedAnchor.Column, 0, columnCount - 1),
                Math.Clamp(requestedAnchor.Row, 0, rowCount - 1));
            if (Math.Abs(clampedRequested.Column - anchorSourceCell.Column) +
                Math.Abs(clampedRequested.Row - anchorSourceCell.Row) == 1 &&
                !blocked.Contains(clampedRequested) &&
                source.FirstOrDefault(item =>
                        !movingKeySet.Contains(item.Key) &&
                        item.Cell.Column == clampedRequested.Column &&
                        item.Cell.Row == clampedRequested.Row) is { } swapped)
            {
                var placements = new Dictionary<string, DesktopIconGridCell>(sourcePlacements, StringComparer.OrdinalIgnoreCase)
                {
                    [anchorKey] = clampedRequested,
                    [swapped.Key] = anchorSourceCell
                };
                var swappedPlacements = new Dictionary<string, DesktopIconGridCell>(StringComparer.OrdinalIgnoreCase)
                {
                    [anchorKey] = clampedRequested
                };
                return new DesktopIconDragLayoutResult(placements, swappedPlacements, clampedRequested);
            }
        }

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
        var working = new Dictionary<DesktopIconGridCell, string>();
        foreach (var item in source)
        {
            if (movingKeySet.Contains(item.Key))
            {
                continue;
            }
            if (!IsInBounds(item.Cell, columnCount, rowCount))
            {
                continue;
            }
            working[item.Cell] = item.Key;
        }
        var reservedTargets = draggedPlacements.Values.ToHashSet();
        // One cascade pass along the squeeze flow. Every occupied cell on the
        // flow enqueues its occupant and is then refilled with the item
        // displaced from the cell before it; reserved targets are jumped over
        // (the moving group lands there later) and cells outside the flow
        // stay untouched, so a horizontal drop pushes the row sideways
        // instead of always cascading down the column.
        var displaced = new Queue<string>();
        var flow = BuildCascadeFlow(anchorCell, direction, columnCount, rowCount).ToArray();
        if (flow.Length > 0)
        {
            CascadeAlongFlow(
                flow,
                blocked,
                reservedTargets,
                working,
                previewPlacements,
                displaced);
        }

        // Reserved targets that lie off the flow (a multi-select formation
        // spanning several directions) still need their cells vacated for the
        // moving group. Free them into the overflow pool below.
        foreach (var cell in reservedTargets)
        {
            if (working.TryGetValue(cell, out var occupant))
            {
                working.Remove(cell);
                displaced.Enqueue(occupant);
            }
        }

        // Anything still displaced after the forward cascade lands in the
        // first free cells anywhere in the grid - normally the cells the
        // moving group just vacated. This keeps drops complete in dense or
        // fully packed grids instead of silently cancelling them.
        if (displaced.Count > 0)
        {
            foreach (var cell in EnumerateCells(columnCount, rowCount).OrderBy(cell => GetGridOrder(cell, rowCount)))
            {
                if (blocked.Contains(cell) ||
                    reservedTargets.Contains(cell) ||
                    working.ContainsKey(cell))
                {
                    continue;
                }
                var key = displaced.Dequeue();
                working[cell] = key;
                previewPlacements[key] = cell;
                if (displaced.Count == 0)
                {
                    break;
                }
            }
            if (displaced.Count > 0)
            {
                return new DesktopIconDragLayoutResult(sourcePlacements, emptyDraggedPlacements, null);
            }
        }

        foreach (var (key, target) in draggedPlacements)
        {
            working[target] = key;
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

    // The order in which displaced icons flow out of the way of the drop.
    // The flow starts at the insertion cell: for a Right squeeze it runs
    // along the row and then down the remaining column-major tail; for a
    // Down squeeze it simply follows the column-major sequence forward.
    private static IEnumerable<DesktopIconGridCell> BuildCascadeFlow(
        DesktopIconGridCell target,
        DesktopIconSqueezeDirection direction,
        int columnCount,
        int rowCount)
    {
        yield return target;
        if (direction == DesktopIconSqueezeDirection.Right)
        {
            for (var column = target.Column + 1; column < columnCount; column++)
            {
                yield return new DesktopIconGridCell(column, target.Row);
            }
        }
        foreach (var cell in EnumerateCells(columnCount, rowCount)
                     .OrderBy(cell => GetGridOrder(cell, rowCount)))
        {
            var order = GetGridOrder(cell, rowCount);
            if (direction == DesktopIconSqueezeDirection.Right)
            {
                if (order > GetGridOrder(new DesktopIconGridCell(columnCount - 1, target.Row), rowCount))
                {
                    yield return cell;
                }
            }
            else if (order > GetGridOrder(target, rowCount))
            {
                yield return cell;
            }
        }
    }

    // Walks one squeeze flow. Each occupied cell on the flow enqueues its
    // occupant (freeing the cell for the item displaced before it); reserved
    // targets are never refilled; cells with an empty queue are left alone so
    // a gap absorbs the insertion without moving anything beyond it. Blocked
    // cells are jumped over, so the run can span reserved gaps without losing
    // or duplicating an icon.
    private static void CascadeAlongFlow(
        IReadOnlyList<DesktopIconGridCell> flow,
        IReadOnlySet<DesktopIconGridCell> blocked,
        IReadOnlySet<DesktopIconGridCell> reservedTargets,
        IDictionary<DesktopIconGridCell, string> working,
        IDictionary<string, DesktopIconGridCell> placements,
        Queue<string> displaced)
    {
        foreach (var cell in flow)
        {
            if (blocked.Contains(cell))
            {
                continue;
            }

            if (working.TryGetValue(cell, out var occupant))
            {
                if (displaced.Count == 0 && !reservedTargets.Contains(cell))
                {
                    // Nothing is being pushed so far and the moving group
                    // does not need this cell; leave the item untouched.
                    continue;
                }
                working.Remove(cell);
                displaced.Enqueue(occupant);
            }

            if (reservedTargets.Contains(cell))
            {
                // The moving group occupies this cell; displaced items pass
                // over it without landing here.
                continue;
            }

            if (displaced.Count > 0)
            {
                var key = displaced.Dequeue();
                working[cell] = key;
                placements[key] = cell;
            }
        }
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
