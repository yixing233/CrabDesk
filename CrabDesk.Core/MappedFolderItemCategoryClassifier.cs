namespace CrabDesk.Core;

/// <summary>
/// Groups direct children of a mapped folder into the compact categories used
/// by its desktop tab strip. This deliberately classifies from the real file
/// path, without opening or inspecting file content.
/// </summary>
public static class MappedFolderItemCategoryClassifier
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avif", ".bmp", ".gif", ".heic", ".ico", ".jpeg", ".jpg",
        ".png", ".svg", ".tif", ".tiff", ".webp"
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".doc", ".docx", ".epub", ".json", ".md", ".odt",
        ".pdf", ".ppt", ".pptx", ".rtf", ".txt", ".xls", ".xlsx"
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".7z", ".bz2", ".cab", ".gz", ".iso", ".rar", ".tar",
        ".tgz", ".xz", ".zip"
    };

    public static MappedFolderItemCategory GetCategory(DesktopItemRef item)
    {
        if (item.Kind == DesktopItemKind.Folder)
        {
            return MappedFolderItemCategory.Folder;
        }

        var extension = Path.GetExtension(item.FileSystemPath ?? item.ParsingName);
        if (ImageExtensions.Contains(extension))
        {
            return MappedFolderItemCategory.Image;
        }
        if (DocumentExtensions.Contains(extension))
        {
            return MappedFolderItemCategory.Document;
        }
        if (ArchiveExtensions.Contains(extension))
        {
            return MappedFolderItemCategory.Archive;
        }

        return MappedFolderItemCategory.Other;
    }

    public static bool Matches(MappedFolderItemCategory category, DesktopItemRef item) =>
        category == MappedFolderItemCategory.All || GetCategory(item) == category;
}
