namespace CrabDesk.Core;

/// <summary>
/// The pure half of the desktop stall attribution: turns four latency readings
/// into a verdict, and maps shell-extension DLLs to the product that owns them.
/// Kept free of Win32 and the registry so it can be tested directly.
/// </summary>
public static class DesktopStallAnalysis
{
    /// <summary>Latency recorded when a step could not run at all.</summary>
    public const int NotMeasured = -1;

    /// <summary>
    /// A stall has to be this much slower than the ordinary-folder control
    /// before it counts as caused by the desktop namespace rather than by
    /// creating a file. Ordinary folders measured 1-2 ms and the desktop
    /// folder 1100-1400 ms on the reporting machine, so the gap is not subtle.
    /// </summary>
    public const int StallThresholdMs = 250;

    /// <summary>
    /// Below this, CrabDesk's own surface is considered responsive. A surface
    /// that is truly blocked by an attached Explorer input queue waits for the
    /// whole stall, so it lands in the same range as the desktop thread.
    /// </summary>
    public const int ResponsiveThresholdMs = 200;

    /// <summary>
    /// Attributes a measured stall. <paramref name="crabDeskSurfaceMs"/> is the
    /// decider: the whole point of detaching the input queues is that Explorer
    /// may stall while CrabDesk keeps answering.
    /// </summary>
    public static DesktopStallVerdict ClassifyVerdict(
        bool desktopFound,
        int idleBaselineMs,
        int ordinaryFolderMs,
        int desktopFolderMs,
        int crabDeskSurfaceMs)
    {
        if (!desktopFound ||
            desktopFolderMs == NotMeasured ||
            ordinaryFolderMs == NotMeasured ||
            crabDeskSurfaceMs == NotMeasured)
        {
            return DesktopStallVerdict.Inconclusive;
        }

        if (desktopFolderMs < StallThresholdMs)
        {
            return DesktopStallVerdict.NoStall;
        }

        // Require the desktop folder to be clearly worse than the control
        // folder as well. Without that, a machine-wide freeze (disk or CPU
        // pressure) would be blamed on the desktop namespace.
        if (desktopFolderMs < ordinaryFolderMs + StallThresholdMs)
        {
            return DesktopStallVerdict.Inconclusive;
        }

        return crabDeskSurfaceMs >= ResponsiveThresholdMs
            ? DesktopStallVerdict.CrabDeskBlocked
            : DesktopStallVerdict.ThirdPartyShellExtensions;
    }

    /// <summary>
    /// Names the product that owns a shell-extension DLL, so the report can
    /// tell the user which program to look at instead of printing a path.
    /// </summary>
    public static string ClassifyProduct(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "未知";
        }

        var value = path.Replace('/', '\\');
        return value switch
        {
            _ when Contains(value, "YunShellExt") || Contains(value, "BaiduNetdisk") => "百度网盘",
            _ when Contains(value, "Xiaomi") || Contains(value, "MiCloud") || Contains(value, "MI\\") => "小米云服务",
            _ when Contains(value, "OneDrive") => "OneDrive",
            _ when Contains(value, "kwpsshellext") || Contains(value, "qingshellext") ||
                   Contains(value, "qingnse") || Contains(value, "kdesktopshellext") ||
                   Contains(value, "kpluginconfigcenter") || Contains(value, "Kingsoft") ||
                   Contains(value, "WPS") => "WPS Office",
            _ when Contains(value, "Bandizip") || Contains(value, "bdzshl") => "Bandizip",
            _ when Contains(value, "Listary") => "Listary",
            _ when Contains(value, "Windhawk") => "Windhawk",
            _ when Contains(value, ".breeze-shell") || Contains(value, "breeze") => "breeze-shell",
            _ when Contains(value, "Quark") => "夸克",
            _ when Contains(value, "7-Zip") => "7-Zip",
            _ when Contains(value, "Foxit") => "Foxit",
            _ when Contains(value, "AcSign") || Contains(value, "Autodesk") => "Autodesk",
            _ when Contains(value, "Sangfor") => "深信服",
            _ when Contains(value, "Bonjour") || Contains(value, "mdnsNSP") => "Bonjour",
            _ when Contains(value, "WeType") || Contains(value, "wetype") => "微信输入法",
            _ when Contains(value, "PhotoBase") || Contains(value, "Windows Photo Viewer") => "Windows 照片查看器",
            _ => "第三方"
        };
    }

    /// <summary>
    /// True for DLLs that ship with Windows and therefore cannot be the
    /// third-party cause. WindowsApps is excluded because Store apps land
    /// there, and the point of the list is to surface non-Microsoft software.
    /// </summary>
    public static bool IsMicrosoftOwnedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        var value = path.Replace('/', '\\');
        return value.StartsWith(@"C:\Windows\", StringComparison.OrdinalIgnoreCase) ||
               value.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith(@"C:\Program Files\Windows ", StringComparison.OrdinalIgnoreCase) ||
               value.Contains(@"\Microsoft\Edge\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extensions worth putting in front of the user: the icon-overlay handlers
    /// registered for the desktop namespace, plus non-Microsoft DLLs loaded in
    /// the desktop's own explorer.exe.
    /// </summary>
    public static IReadOnlyList<ShellExtensionEntry> FilterRelevantExtensions(
        IEnumerable<ShellExtensionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries
            .Where(entry => entry.Kind == ShellExtensionKind.IconOverlay ||
                            !IsMicrosoftOwnedPath(entry.Path))
            .GroupBy(entry => (entry.Kind, entry.Name, entry.Path), StringTupleComparer.Instance)
            .Select(group => group.First())
            .OrderBy(entry => entry.Kind)
            .ThenBy(entry => entry.Product, StringComparer.Ordinal)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Counts how many registered handlers belong to each product, so the
    /// report can say "百度网盘 (7 项)" rather than listing seven rows.
    /// </summary>
    public static IReadOnlyList<(string Product, int Count)> CountByProduct(
        IEnumerable<ShellExtensionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries
            .GroupBy(entry => entry.Product, StringComparer.Ordinal)
            .Select(group => (Product: group.Key, Count: group.Count()))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Product, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool Contains(string value, string token) =>
        value.Contains(token, StringComparison.OrdinalIgnoreCase);

    private sealed class StringTupleComparer : IEqualityComparer<(ShellExtensionKind, string, string)>
    {
        internal static readonly StringTupleComparer Instance = new();

        public bool Equals((ShellExtensionKind, string, string) x, (ShellExtensionKind, string, string) y) =>
            x.Item1 == y.Item1 &&
            string.Equals(x.Item2, y.Item2, StringComparison.Ordinal) &&
            string.Equals(x.Item3, y.Item3, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((ShellExtensionKind, string, string) obj) =>
            HashCode.Combine(obj.Item1, obj.Item2, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item3));
    }
}
