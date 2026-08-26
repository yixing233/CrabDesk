using CrabDesk.Core;

namespace CrabDesk.Runtime;

/// <summary>
/// Validates a real desktop folder as an OLE drop target without touching the
/// filesystem. The shell operation performs the final existence/permission
/// checks, while this policy keeps impossible self/descendant moves out of the
/// drag route and its visual feedback.
/// </summary>
internal static class DesktopFolderDropPolicy
{
    internal static bool CanAccept(
        IReadOnlyCollection<DesktopItemRef> draggedItems,
        DesktopItemRef? target)
    {
        if (draggedItems.Count == 0 ||
            target is not { Kind: DesktopItemKind.Folder, FileSystemPath: not null } ||
            !TryNormalizePath(target.FileSystemPath, out var targetPath))
        {
            return false;
        }

        var targetKey = target.Key.ToString();
        foreach (var draggedItem in draggedItems)
        {
            if (draggedItem.FileSystemPath is not { } sourcePathValue ||
                string.Equals(
                    draggedItem.Key.ToString(),
                    targetKey,
                    StringComparison.OrdinalIgnoreCase) ||
                !TryNormalizePath(sourcePathValue, out var sourcePath) ||
                string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (draggedItem.Kind == DesktopItemKind.Folder &&
                IsDescendantPath(targetPath, sourcePath))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryNormalizePath(string path, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return normalizedPath.Length > 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsDescendantPath(string candidatePath, string parentPath)
    {
        var parentWithSeparator = parentPath.EndsWith(Path.DirectorySeparatorChar) ||
                                  parentPath.EndsWith(Path.AltDirectorySeparatorChar)
            ? parentPath
            : parentPath + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(parentWithSeparator, StringComparison.OrdinalIgnoreCase);
    }
}
