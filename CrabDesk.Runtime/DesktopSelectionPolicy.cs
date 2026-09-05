using CrabDesk.Core;

namespace CrabDesk.Runtime;

internal enum DesktopSelectionGesture
{
    PrimaryItem,
    Marquee,
    ContextItem,

    /// <summary>
    /// Shift+click extending from the surface anchor to the clicked item. New
    /// members must be appended: the shared policy tests address the gesture by
    /// its numeric value.
    /// </summary>
    RangeItem
}

internal sealed record DesktopDeleteSelection(
    IReadOnlyList<DesktopItemRef> DeletableItems,
    int SelectedCount,
    int BlockedCount);

internal static class DesktopSelectionPolicy
{
    internal static bool PreserveExistingSelection(
        DesktopSelectionGesture gesture,
        bool additive,
        bool targetAlreadySelected) => gesture switch
    {
        DesktopSelectionGesture.PrimaryItem => additive || targetAlreadySelected,
        DesktopSelectionGesture.Marquee => additive,
        DesktopSelectionGesture.ContextItem => targetAlreadySelected,

        // A range lives inside one surface, so a plain Shift+click owns the
        // whole desktop selection exactly like a plain marquee. Ctrl+Shift
        // extends instead of replacing, and then other surfaces keep theirs.
        DesktopSelectionGesture.RangeItem => additive,
        _ => false
    };

    /// <summary>
    /// Returns the inclusive run of keys between the anchor and the clicked
    /// item, in the surface's own reading order. An anchor that is no longer
    /// present — filtered away by a search, scrolled out of a box, moved to
    /// another monitor — yields an empty range, which callers treat as a plain
    /// click. Validating on use is what lets every surface leave stale anchors
    /// alone instead of pruning them from each selection mutation.
    /// </summary>
    internal static IReadOnlyList<string> BuildRangeSelectionKeys(
        IReadOnlyList<string> orderedKeys,
        string? anchorKey,
        string targetKey)
    {
        if (anchorKey is null)
        {
            return [];
        }

        var anchorIndex = IndexOf(orderedKeys, anchorKey);
        var targetIndex = IndexOf(orderedKeys, targetKey);
        if (anchorIndex < 0 || targetIndex < 0)
        {
            return [];
        }

        var start = Math.Min(anchorIndex, targetIndex);
        var end = Math.Max(anchorIndex, targetIndex);
        var range = new string[end - start + 1];
        for (var index = start; index <= end; index++)
        {
            range[index - start] = orderedKeys[index];
        }

        return range;
    }

    private static int IndexOf(IReadOnlyList<string> keys, string key)
    {
        for (var index = 0; index < keys.Count; index++)
        {
            if (string.Equals(keys[index], key, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    internal static DesktopDeleteSelection BuildDeleteSelection(
        IEnumerable<DesktopItemRef> selectedItems,
        IEnumerable<DesktopItemRef> deletableItems)
    {
        var selected = selectedItems
            .GroupBy(
                item => item.FileSystemPath ?? item.Key.ToString(),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var deletable = deletableItems
            .Where(item => item.FileSystemPath is not null)
            .GroupBy(item => item.FileSystemPath!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var deletablePaths = deletable
            .Select(item => item.FileSystemPath!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var blockedCount = selected.Count(item =>
            item.FileSystemPath is null ||
            !deletablePaths.Contains(item.FileSystemPath));

        return new DesktopDeleteSelection(
            deletable,
            selected.Length,
            blockedCount);
    }
}
