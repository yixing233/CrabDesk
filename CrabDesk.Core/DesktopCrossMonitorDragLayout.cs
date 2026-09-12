namespace CrabDesk.Core;

/// <summary>Plans a whole selection entering another desktop grid, without file-path assumptions.</summary>
public static class DesktopCrossMonitorDragLayout
{
    public static DesktopIconDragLayoutResult Calculate(
        IEnumerable<DesktopIconGridItem> targetItems,
        IReadOnlyList<DesktopIconGridItem> incoming,
        string anchorKey,
        DesktopIconGridCell targetCell,
        int columns,
        int rows)
    {
        var keys = incoming.Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stationary = targetItems.Where(item => !keys.Contains(item.Key)).ToArray();
        var result = DesktopIconDragLayoutEngine.Calculate(
            stationary.Concat(incoming), keys, anchorKey, targetCell, columns, rows,
            allowSwap: false);
        if (result.IsValid || !keys.Contains(anchorKey)) return result;

        // A wide/tall source formation may not fit a smaller monitor. Compact
        // the entire selection there, keeping the grabbed item first. A full
        // target still fails atomically instead of moving only some items.
        var ordered = incoming.OrderBy(item => string.Equals(item.Key, anchorKey,
                StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(item => item.Cell.Column).ThenBy(item => item.Cell.Row)
            .Select(item => item.Key);
        return DesktopIconDragLayoutEngine.CalculateInsertion(stationary, ordered, targetCell, columns, rows);
    }
}
