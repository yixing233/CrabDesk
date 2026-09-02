namespace CrabDesk.Runtime;

/// <summary>
/// Decides which clipboard entries a paste onto the desktop or into a mapped
/// box should process. Pasting a cut item back into the folder it already lives
/// in is a no-op in Explorer, so those sources are skipped instead of being
/// moved onto themselves.
/// </summary>
internal static class DesktopPastePolicy
{
    internal static bool CanPasteSource(
        string sourcePath,
        string destinationDirectory,
        bool move)
    {
        if (!move || string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destinationDirectory))
        {
            return !string.IsNullOrWhiteSpace(sourcePath);
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
        return parent is not null &&
            !string.Equals(
                Normalize(parent),
                Normalize(destinationDirectory),
                StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => Path
        .GetFullPath(path)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
