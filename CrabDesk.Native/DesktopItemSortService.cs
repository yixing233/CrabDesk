using CrabDesk.Core;

namespace CrabDesk.Native;

/// <summary>
/// Keeps the replacement desktop layer's ordering rules in one testable place.
/// Explorer represents a missing Date modified value as an empty property;
/// empty values compare before real dates in ascending order and after them
/// in descending order.
/// </summary>
public static class DesktopItemSortService
{
    public static IOrderedEnumerable<DesktopItemRef> Order(
        IReadOnlyList<DesktopItemRef> items,
        DesktopIconSortState sort) => sort.Mode switch
    {
        DesktopIconSortMode.Size when sort.Descending => items
            .OrderByDescending(item => GetFileSize(item) ?? -1)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase),
        DesktopIconSortMode.Size => items
            .OrderBy(item => GetFileSize(item) ?? -1)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase),
        DesktopIconSortMode.Type when sort.Descending => items
            .OrderByDescending(GetItemTypeKey, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase),
        DesktopIconSortMode.Type => items
            .OrderBy(GetItemTypeKey, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase),
        DesktopIconSortMode.Modified when sort.Descending => items
            .OrderByDescending(item => item.ModifiedAt is not null)
            .ThenByDescending(item => item.ModifiedAt)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase),
        DesktopIconSortMode.Modified => items
            .OrderBy(item => item.ModifiedAt is not null)
            .ThenBy(item => item.ModifiedAt)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase),
        DesktopIconSortMode.Name when sort.Descending => items
            .OrderByDescending(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase),
        _ => items.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
    };

    private static long? GetFileSize(DesktopItemRef item)
    {
        if (item.FileSystemPath is not { Length: > 0 } path || item.Kind == DesktopItemKind.Folder)
        {
            return null;
        }

        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string GetItemTypeKey(DesktopItemRef item) => item.Kind switch
    {
        DesktopItemKind.Folder => "folder",
        DesktopItemKind.Shortcut => "shortcut",
        DesktopItemKind.Shell => "system",
        _ => Path.GetExtension(item.FileSystemPath ?? item.DisplayName)
    };
}
