using System.IO;

namespace CrabDesk.Core;

/// <summary>
/// Keeps desktop item labels and file operations consistent, including names
/// whose first character is a dot (for example .gitignore and .minecraft).
/// </summary>
public static class DesktopItemName
{
    public static string GetDisplayName(string path, bool isDirectory)
    {
        var name = Path.GetFileName(path);
        return isDirectory ? name : GetBaseName(name);
    }

    public static string GetBaseName(string path)
    {
        var name = Path.GetFileName(path);
        var baseName = Path.GetFileNameWithoutExtension(name);
        // .gitignore has an extension according to System.IO, but no stem.
        // Treat it as a complete filename instead of producing an empty label.
        return string.IsNullOrEmpty(baseName) ? name : baseName;
    }

    public static (string Stem, string Extension) SplitFileName(string path)
    {
        var name = Path.GetFileName(path);
        var stem = GetBaseName(name);
        return string.Equals(stem, name, StringComparison.Ordinal)
            ? (stem, string.Empty)
            : (stem, Path.GetExtension(name));
    }
}
