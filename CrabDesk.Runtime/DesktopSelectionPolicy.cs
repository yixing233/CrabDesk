using CrabDesk.Core;

namespace CrabDesk.Runtime;

internal enum DesktopSelectionGesture
{
    PrimaryItem,
    Marquee,
    ContextItem
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
        _ => false
    };

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
