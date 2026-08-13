using System.Runtime.InteropServices;

namespace CrabDesk.Native;

public enum DesktopIconSortMode
{
    Name,
    Size,
    Type,
    Modified
}

/// <summary>
/// The active desktop ordering as reported by Explorer.  Descending is part
/// of the Shell sort-column syntax, so it must travel with the property.
/// </summary>
public readonly record struct DesktopIconSortState(
    DesktopIconSortMode Mode,
    bool Descending);

/// <summary>
/// Read-only snapshot of the desktop options exposed by Explorer's live view.
/// </summary>
public readonly record struct DesktopIconViewState(
    DesktopIconSortState Sort,
    int? IconSize,
    bool DesktopIconsVisible,
    bool AutoArrange,
    string Signature);

/// <summary>
/// Reads the Windows desktop icon-size preference and forwards an explicit
/// Ctrl+wheel gesture. It never enumerates, reads from, or writes to
/// Explorer's desktop ListView memory.
/// </summary>
public static class DesktopIconPositionService
{
    private const string DesktopBagPath = @"Software\Microsoft\Windows\Shell\Bags\1\Desktop";
    private const int ShellWindowClassDesktop = 8;
    private const int ShellWindowFindNeedDispatch = 1;
    private const uint FolderFlagAutoArrange = 0x00000001;
    private const uint FolderFlagNoIcons = 0x00001000;
    private const uint LvmFirst = 0x1000;
    private const uint LvmGetItemSpacing = LvmFirst + 51;
    private const uint WmMouseWheel = 0x020A;
    private const uint MkControl = 0x0008;
    private const uint MessageTimeoutMilliseconds = 500;
    private static readonly Guid ShellItemPropertyFormat =
        new("B725F130-47EF-101A-A5F1-02608C9EEBAC");
    private static readonly Guid ShellDatePropertyFormat =
        new("F29F85E0-4FF9-1068-AB91-08002B27B3D9");

    /// <summary>
    /// Reads the live desktop view first. Explorer can apply context-menu
    /// changes before it persists the matching Bag value.
    /// </summary>
    public static DesktopIconViewState GetDesktopViewState()
    {
        if (TryReadExplorerDesktopView(out var explorerView))
        {
            var sort = DecodeDesktopSortColumns(explorerView.SortColumns);
            var iconSize = explorerView.IconSize is { } size and > 0
                ? Math.Clamp(size, 16, 256)
                : GetPersistedDesktopIconSize();
            var iconsVisible = explorerView.FolderFlags is { } flags
                ? (flags & FolderFlagNoIcons) == 0
                : true;
            var autoArrange = explorerView.FolderFlags is { } folderFlags &&
                IsAutoArrangeEnabled(folderFlags);
            var signature = $"shell:{explorerView.SortColumns.Trim()}|" +
                $"size:{iconSize?.ToString() ?? string.Empty}|" +
                $"flags:{explorerView.FolderFlags?.ToString("X8") ?? "unknown"}";
            return new DesktopIconViewState(sort, iconSize, iconsVisible, autoArrange, signature);
        }

        var persistedSort = GetDesktopSortValue();
        var persistedIconSize = GetPersistedDesktopIconSize();
        return new DesktopIconViewState(
            new DesktopIconSortState(DecodeDesktopSortMode(persistedSort), false),
            persistedIconSize,
            true,
            false,
            $"registry:{(persistedSort is { Length: > 0 } ? Convert.ToHexString(persistedSort) : string.Empty)}|" +
            $"size:{persistedIconSize?.ToString() ?? string.Empty}");
    }

    public static int? GetDesktopIconSize() => GetDesktopViewState().IconSize;

    /// <summary>
    /// Decodes Explorer's persisted desktop sort property.  The shell stores
    /// its property key as a binary REG value; an all-zero key represents the
    /// default Name ordering.
    /// </summary>
    public static DesktopIconSortMode GetDesktopSortMode()
    {
        return GetDesktopViewState().Sort.Mode;
    }

    public static DesktopIconSortState GetDesktopSortState() => GetDesktopViewState().Sort;

    public static bool AreDesktopIconsVisible() => GetDesktopViewState().DesktopIconsVisible;

    /// <summary>
    /// Explorer exposes automatic arrangement through the desktop folder flags.
    /// Kept public so the exact Shell bit can be regression-tested without
    /// requiring a live desktop COM view.
    /// </summary>
    public static bool IsAutoArrangeEnabled(uint folderFlags) =>
        (folderFlags & FolderFlagAutoArrange) != 0;

    public static DesktopIconSortMode DecodeDesktopSortMode(byte[]? value)
    {
        if (value is null || value.Length == 0)
        {
            return DesktopIconSortMode.Name;
        }

        return ContainsPropertyKey(value, ShellItemPropertyFormat, 12)
            ? DesktopIconSortMode.Size
            : ContainsPropertyKey(value, ShellItemPropertyFormat, 4)
                ? DesktopIconSortMode.Type
                : ContainsPropertyKey(value, ShellDatePropertyFormat, 14)
                    ? DesktopIconSortMode.Modified
                    : DesktopIconSortMode.Name;
    }

    /// <summary>
    /// Decodes IShellFolderViewDual3.SortColumns, for example
    /// <c>prop:-System.DateModified;</c>. A leading minus denotes descending
    /// order and is not represented in Explorer's persisted Sort value.
    /// </summary>
    public static DesktopIconSortState DecodeDesktopSortColumns(string? sortColumns)
    {
        var token = sortColumns?
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(token))
        {
            return new DesktopIconSortState(DesktopIconSortMode.Name, false);
        }

        if (token.StartsWith("prop:", StringComparison.OrdinalIgnoreCase))
        {
            token = token[5..];
        }
        var descending = token.StartsWith("-", StringComparison.Ordinal);
        token = token.TrimStart('-');
        var mode = token.ToLowerInvariant() switch
        {
            "system.size" => DesktopIconSortMode.Size,
            "system.itemtype" or "system.itemtypetext" => DesktopIconSortMode.Type,
            "system.datemodified" => DesktopIconSortMode.Modified,
            _ => DesktopIconSortMode.Name
        };
        return new DesktopIconSortState(mode, descending);
    }

    /// <summary>
    /// Provides a stable, read-only change token for Explorer's live desktop
    /// view. The registry remains a fallback for an unavailable Shell view.
    /// </summary>
    public static string GetDesktopSortSignature() => GetDesktopViewState().Signature;

    public static bool ForwardControlMouseWheel(IntPtr listView, int screenX, int screenY, int delta)
    {
        if (listView == IntPtr.Zero || !NativeMethods.IsWindow(listView) || delta == 0)
        {
            return false;
        }

        var wheel = unchecked((uint)(ushort)(short)delta);
        var keysAndDelta = new IntPtr(unchecked((int)((wheel << 16) | MkControl)));
        var coordinates = unchecked((uint)(ushort)(short)screenX) |
            (unchecked((uint)(ushort)(short)screenY) << 16);
        return NativeMethods.PostMessage(
            listView,
            WmMouseWheel,
            keysAndDelta,
            new IntPtr(unchecked((int)coordinates)));
    }

    public static System.Drawing.Size GetItemSpacing(IntPtr listView) =>
        TryGetItemSpacing(listView, out var spacing)
            ? spacing
            : new System.Drawing.Size(88, 96);

    /// <summary>
    /// Reads the live icon grid spacing without replacing a transient Shell
    /// timeout with a different layout. Callers that render an existing icon
    /// surface can retain their last valid spacing when this returns false.
    /// </summary>
    public static bool TryGetItemSpacing(IntPtr listView, out System.Drawing.Size spacing)
    {
        spacing = default;
        if (listView == IntPtr.Zero || !NativeMethods.IsWindow(listView) ||
            NativeMethods.SendMessageTimeout(
                listView,
                LvmGetItemSpacing,
                IntPtr.Zero,
                IntPtr.Zero,
                NativeMethods.SmtoAbortIfHung,
                MessageTimeoutMilliseconds,
                out var result) == IntPtr.Zero)
        {
            return false;
        }

        var packed = result.ToInt64();
        var horizontal = unchecked((ushort)(packed & 0xffff));
        var vertical = unchecked((ushort)((packed >> 16) & 0xffff));
        if (horizontal == 0 || vertical == 0)
        {
            return false;
        }

        spacing = new System.Drawing.Size(horizontal, vertical);
        return true;
    }

    private static int? GetPersistedDesktopIconSize()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(DesktopBagPath);
            return key?.GetValue("IconSize") is int value ? Math.Clamp(value, 16, 256) : null;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? GetDesktopSortValue()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(DesktopBagPath);
            return key?.GetValue("Sort") as byte[];
        }
        catch
        {
            return null;
        }
    }

    private static bool ContainsPropertyKey(byte[] value, Guid format, int propertyId)
    {
        if (value.Length < 20)
        {
            return false;
        }

        var formatBytes = format.ToByteArray();
        for (var offset = 0; offset <= value.Length - 20; offset++)
        {
            if (!value.AsSpan(offset, 16).SequenceEqual(formatBytes) ||
                BitConverter.ToInt32(value, offset + 16) != propertyId)
            {
                continue;
            }
            return true;
        }
        return false;
    }

    private static bool TryReadExplorerDesktopView(out ExplorerDesktopView explorerView)
    {
        explorerView = default;
        object? shellObject = null;
        IShellWindows? shellWindows = null;
        IWebBrowser? desktopBrowser = null;
        IShellFolderViewDual3? desktopView = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            shellObject = shellType is null ? null : Activator.CreateInstance(shellType);
            if (shellObject is not IShellDispatch shell ||
                shell.Windows(out shellWindows) != 0 ||
                shellWindows is null)
            {
                return false;
            }

            object location = null!;
            object root = null!;
            if (shellWindows.FindWindowSW(
                    ref location,
                    ref root,
                    ShellWindowClassDesktop,
                    out _,
                    ShellWindowFindNeedDispatch,
                    out desktopBrowser) != 0 ||
                desktopBrowser is null ||
                desktopBrowser.GetDocument(out desktopView) != 0 ||
                desktopView is null ||
                desktopView.GetSortColumns(out var sortColumns) != 0)
            {
                return false;
            }

            var iconSize = desktopView.GetIconSize(out var liveIconSize) == 0
                ? liveIconSize
                : (int?)null;
            var folderFlags = desktopView.GetFolderFlags(out var liveFolderFlags) == 0
                ? liveFolderFlags
                : (uint?)null;
            explorerView = new ExplorerDesktopView(sortColumns ?? string.Empty, iconSize, folderFlags);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseComObject(desktopView);
            ReleaseComObject(desktopBrowser);
            ReleaseComObject(shellWindows);
            ReleaseComObject(shellObject);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
        {
            return;
        }
        try
        {
            Marshal.FinalReleaseComObject(value);
        }
        catch (InvalidComObjectException)
        {
        }
    }

    private readonly record struct ExplorerDesktopView(
        string SortColumns,
        int? IconSize,
        uint? FolderFlags);

    // The Shell automation interfaces below are used only to read the live
    // desktop view. They never issue Explorer commands or inspect ListView
    // process memory.
    [ComImport]
    [Guid("D8F015C0-C278-11CE-A49E-444553540000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellDispatch
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, uint lcid, out IntPtr typeInfo);
        [PreserveSig] int GetIDsOfNames(ref Guid iid, IntPtr names, uint count, uint lcid, IntPtr dispatchIds);
        [PreserveSig] int Invoke(int dispatchId, ref Guid iid, uint lcid, ushort flags, IntPtr parameters, IntPtr result, IntPtr exception, IntPtr argumentError);
        [PreserveSig] int GetApplication([MarshalAs(UnmanagedType.IDispatch)] out object application);
        [PreserveSig] int GetParent([MarshalAs(UnmanagedType.IDispatch)] out object parent);
        [PreserveSig] int NameSpace([MarshalAs(UnmanagedType.Struct)] object directory, [MarshalAs(UnmanagedType.IDispatch)] out object folder);
        [PreserveSig] int BrowseForFolder(int owner, [MarshalAs(UnmanagedType.BStr)] string title, int options, [MarshalAs(UnmanagedType.Struct)] object root, [MarshalAs(UnmanagedType.IDispatch)] out object folder);
        [PreserveSig] int Windows([MarshalAs(UnmanagedType.Interface)] out IShellWindows windows);
    }

    [ComImport]
    [Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellWindows
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, uint lcid, out IntPtr typeInfo);
        [PreserveSig] int GetIDsOfNames(ref Guid iid, IntPtr names, uint count, uint lcid, IntPtr dispatchIds);
        [PreserveSig] int Invoke(int dispatchId, ref Guid iid, uint lcid, ushort flags, IntPtr parameters, IntPtr result, IntPtr exception, IntPtr argumentError);
        [PreserveSig] int Count(out int count);
        [PreserveSig] int Item([MarshalAs(UnmanagedType.Struct)] object index, [MarshalAs(UnmanagedType.IDispatch)] out object folder);
        [PreserveSig] int NewEnum([MarshalAs(UnmanagedType.IUnknown)] out object value);
        [PreserveSig] int Register([MarshalAs(UnmanagedType.IDispatch)] object dispatch, int window, int shellWindowClass, out int cookie);
        [PreserveSig] int RegisterPending(int threadId, [MarshalAs(UnmanagedType.Struct)] ref object location, [MarshalAs(UnmanagedType.Struct)] ref object root, int shellWindowClass, out int cookie);
        [PreserveSig] int Revoke(int cookie);
        [PreserveSig] int OnNavigate(int cookie, [MarshalAs(UnmanagedType.Struct)] ref object location);
        [PreserveSig] int OnActivated(int cookie, short active);
        [PreserveSig] int FindWindowSW(
            [MarshalAs(UnmanagedType.Struct)] ref object location,
            [MarshalAs(UnmanagedType.Struct)] ref object root,
            int shellWindowClass,
            out int window,
            int options,
            [MarshalAs(UnmanagedType.Interface)] out IWebBrowser desktop);
    }

    [ComImport]
    [Guid("EAB22AC1-30C1-11CF-A7EB-0000C05BAE0B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWebBrowser
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, uint lcid, out IntPtr typeInfo);
        [PreserveSig] int GetIDsOfNames(ref Guid iid, IntPtr names, uint count, uint lcid, IntPtr dispatchIds);
        [PreserveSig] int Invoke(int dispatchId, ref Guid iid, uint lcid, ushort flags, IntPtr parameters, IntPtr result, IntPtr exception, IntPtr argumentError);
        [PreserveSig] int GoBack();
        [PreserveSig] int GoForward();
        [PreserveSig] int GoHome();
        [PreserveSig] int GoSearch();
        [PreserveSig] int Navigate([MarshalAs(UnmanagedType.BStr)] string url, IntPtr flags, IntPtr targetFrameName, IntPtr postData, IntPtr headers);
        [PreserveSig] int Refresh();
        [PreserveSig] int Refresh2(IntPtr level);
        [PreserveSig] int Stop();
        [PreserveSig] int GetApplication([MarshalAs(UnmanagedType.IDispatch)] out object application);
        [PreserveSig] int GetParent([MarshalAs(UnmanagedType.IDispatch)] out object parent);
        [PreserveSig] int GetContainer([MarshalAs(UnmanagedType.IDispatch)] out object container);
        [PreserveSig] int GetDocument([MarshalAs(UnmanagedType.Interface)] out IShellFolderViewDual3 document);
    }

    [ComImport]
    [Guid("29EC8E6C-46D3-411F-BAAA-611A6C9CAC66")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolderViewDual3
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, uint lcid, out IntPtr typeInfo);
        [PreserveSig] int GetIDsOfNames(ref Guid iid, IntPtr names, uint count, uint lcid, IntPtr dispatchIds);
        [PreserveSig] int Invoke(int dispatchId, ref Guid iid, uint lcid, ushort flags, IntPtr parameters, IntPtr result, IntPtr exception, IntPtr argumentError);
        [PreserveSig] int GetApplication([MarshalAs(UnmanagedType.IDispatch)] out object application);
        [PreserveSig] int GetParent([MarshalAs(UnmanagedType.IDispatch)] out object parent);
        [PreserveSig] int GetFolder([MarshalAs(UnmanagedType.IDispatch)] out object folder);
        [PreserveSig] int SelectedItems([MarshalAs(UnmanagedType.IDispatch)] out object items);
        [PreserveSig] int GetFocusedItem([MarshalAs(UnmanagedType.IDispatch)] out object item);
        [PreserveSig] int SelectItem(IntPtr item, int flags);
        [PreserveSig] int PopupItemMenu(IntPtr item, [MarshalAs(UnmanagedType.Struct)] object x, [MarshalAs(UnmanagedType.Struct)] object y, [MarshalAs(UnmanagedType.BStr)] out string command);
        [PreserveSig] int GetScript([MarshalAs(UnmanagedType.IDispatch)] out object script);
        [PreserveSig] int GetViewOptions(out int options);
        [PreserveSig] int GetCurrentViewMode(out uint mode);
        [PreserveSig] int SetCurrentViewMode(uint mode);
        [PreserveSig] int SelectItemRelative(int relative);
        [PreserveSig] int GetGroupBy([MarshalAs(UnmanagedType.BStr)] out string groupBy);
        [PreserveSig] int SetGroupBy([MarshalAs(UnmanagedType.BStr)] string groupBy);
        [PreserveSig] int GetFolderFlags(out uint flags);
        [PreserveSig] int SetFolderFlags(uint flags);
        [PreserveSig] int GetSortColumns([MarshalAs(UnmanagedType.BStr)] out string sortColumns);
        [PreserveSig] int SetSortColumns([MarshalAs(UnmanagedType.BStr)] string sortColumns);
        [PreserveSig] int SetIconSize(int iconSize);
        [PreserveSig] int GetIconSize(out int iconSize);
    }

}
