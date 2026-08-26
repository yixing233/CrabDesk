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

internal sealed record DesktopDeleteConfirmation(
    string Title,
    string Message,
    string PrimaryText);

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

    internal static DesktopDeleteConfirmation BuildDeleteConfirmation(
        DesktopDeleteSelection selection)
    {
        if (selection.DeletableItems.Count == 0)
        {
            throw new ArgumentException(
                "A delete confirmation requires at least one deletable item.",
                nameof(selection));
        }

        var title = selection.DeletableItems.Count == 1
            ? $"确认删除“{selection.DeletableItems[0].DisplayName}”？"
            : $"确认删除 {selection.DeletableItems.Count} 个{GetDeleteItemNoun(selection.DeletableItems)}？";
        var skipped = selection.BlockedCount > 0
            ? $"{Environment.NewLine}其中 {selection.BlockedCount} 个只读或系统项目不会被删除。"
            : string.Empty;

        return new DesktopDeleteConfirmation(
            title,
            $"删除后将移入回收站，可从回收站恢复。{skipped}",
            "删除");
    }

    private static string GetDeleteItemNoun(IReadOnlyList<DesktopItemRef> items)
    {
        if (items.All(item => item.Kind == DesktopItemKind.Folder))
        {
            return "文件夹";
        }

        return items.All(item => item.Kind is DesktopItemKind.File or DesktopItemKind.Shortcut)
            ? "文件"
            : "项目";
    }
}
