using System.Runtime.InteropServices;

namespace CrabDesk.Native;

public enum DesktopIconSortMode
{
    Name,
    Size,
    Type,
    Modified,
    Created
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
    string Signature,
    bool HasAuthoritativeSort = false,
    bool HasLiveSortColumns = false);

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
    private static readonly object ShellReadCacheGate = new();
    private static DesktopIconViewState _cachedViewState;
    private static bool _hasCachedViewState;
    private static IntPtr _cachedSpacingListView;
    private static System.Drawing.Size _cachedSpacing;
    private static bool _cachedSpacingValid;

    /// <summary>
    /// Reads the live desktop view first. Explorer can apply context-menu
    /// changes before it persists the matching Bag value. Callers that watch
    /// for a change use this, so it always performs the live read and only
    /// refreshes the cache the render path shares.
    /// </summary>
    public static DesktopIconViewState GetDesktopViewState()
    {
        var state = ReadDesktopViewState();
        lock (ShellReadCacheGate)
        {
            if (_hasCachedViewState)
            {
                state = PreserveLastKnownSortWhenSnapshotHasNoLiveSort(
                    state,
                    _cachedViewState);
            }
            _cachedViewState = state;
            _hasCachedViewState = true;
        }
        return state;
    }

    /// <summary>
    /// A concurrent COM read can finish late with empty or unrecognized
    /// SortColumns after another reader has already published a valid sort.
    /// Such a snapshot may update the other view fields, but cannot downgrade
    /// the shared last-known sort.
    /// </summary>
    internal static DesktopIconViewState PreserveLastKnownSortWhenSnapshotHasNoLiveSort(
        DesktopIconViewState snapshot,
        DesktopIconViewState cached) =>
        snapshot.HasLiveSortColumns || !cached.HasAuthoritativeSort
            ? snapshot
            : snapshot with
            {
                Sort = cached.Sort,
                HasAuthoritativeSort = true
            };

    /// <summary>
    /// Returns the last desktop view published by a live reader. Rendering
    /// must never cross into explorer.exe over COM; before the first live
    /// snapshot it uses the persisted registry state instead.
    /// </summary>
    public static DesktopIconViewState GetCachedDesktopViewState()
    {
        lock (ShellReadCacheGate)
        {
            if (_hasCachedViewState)
            {
                return _cachedViewState;
            }
        }

        var persistedState = ReadPersistedDesktopViewState();
        lock (ShellReadCacheGate)
        {
            if (_hasCachedViewState)
            {
                return _cachedViewState;
            }
            _cachedViewState = persistedState;
            _hasCachedViewState = true;
            return _cachedViewState;
        }
    }

    /// <summary>
    /// Drops cached Shell reads after CrabDesk asks Explorer to change the
    /// desktop view. Non-blocking render readers use persisted state until the
    /// next explicit live reader publishes a new snapshot.
    /// </summary>
    public static void InvalidateCachedDesktopView()
    {
        lock (ShellReadCacheGate)
        {
            _hasCachedViewState = false;
            _cachedSpacingListView = IntPtr.Zero;
            _cachedSpacing = default;
            _cachedSpacingValid = false;
        }
    }

    private static DesktopIconViewState ReadDesktopViewState()
    {
        if (TryReadExplorerDesktopView(out var explorerView))
        {
            // Explorer can transiently return empty or unknown SortColumns while
            // applying a desktop change. Keep the last known order, but do not
            // mistake that fallback for a fresh authoritative sort command.
            var fallback = GetLastKnownDesktopSortState();
            var hasLiveSort = TryDecodeDesktopSortColumns(explorerView.SortColumns, out var liveSort);
            var sort = hasLiveSort ? liveSort : fallback.Sort;
            var hasAuthoritativeSort = hasLiveSort || fallback.HasAuthoritativeSort;
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
            return new DesktopIconViewState(
                sort,
                iconSize,
                iconsVisible,
                autoArrange,
                signature,
                hasAuthoritativeSort,
                hasLiveSort);
        }

        return ReadPersistedDesktopViewState();
    }

    private static (DesktopIconSortState Sort, bool HasAuthoritativeSort) GetLastKnownDesktopSortState()
    {
        lock (ShellReadCacheGate)
        {
            if (_hasCachedViewState)
            {
                return (_cachedViewState.Sort, _cachedViewState.HasAuthoritativeSort);
            }
        }

        var persistedValue = GetDesktopSortValue();
        var isKnown = TryDecodePersistedDesktopSortMode(persistedValue, out var mode);
        return (new DesktopIconSortState(mode, false), isKnown);
    }

    private static DesktopIconViewState ReadPersistedDesktopViewState()
    {
        var persistedSort = GetDesktopSortValue();
        var hasAuthoritativeSort = TryDecodePersistedDesktopSortMode(persistedSort, out var mode);
        var persistedIconSize = GetPersistedDesktopIconSize();
        return new DesktopIconViewState(
            new DesktopIconSortState(mode, false),
            persistedIconSize,
            true,
            false,
            $"registry:{(persistedSort is { Length: > 0 } ? Convert.ToHexString(persistedSort) : string.Empty)}|" +
            $"size:{persistedIconSize?.ToString() ?? string.Empty}",
            hasAuthoritativeSort);
    }

    public static int? GetDesktopIconSize() => GetDesktopViewState().IconSize;

    /// <summary>
    /// Decodes Explorer's persisted desktop sort property. The shell stores
    /// its property key as a binary REG value. A zero-filled value is treated
    /// as the Name fallback by the decoder, but is not an authoritative sort
    /// selection when Explorer's live SortColumns is temporarily unavailable.
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
        TryDecodePersistedDesktopSortMode(value, out var mode);
        return mode;
    }

    internal static bool TryDecodePersistedDesktopSortMode(
        byte[]? value,
        out DesktopIconSortMode mode)
    {
        mode = DesktopIconSortMode.Name;
        if (value is null || value.Length < 20)
        {
            return false;
        }

        // An all-zero registry value is only a fallback/default representation,
        // not proof that Name is the active order. In particular, Explorer can
        // transiently expose empty SortColumns while applying a drag or view
        // change; treating zeroes as authoritative would replace a known
        // Created-time order with A-Z. Explicit ItemNameDisplay is authoritative.
        if (value.All(static item => item == 0))
        {
            return false;
        }
        if (ContainsPropertyKey(value, ShellItemPropertyFormat, 10))
        {
            return true;
        }

        // PKEY_DateCreated (15) and PKEY_DateModified (14) use the Shell item
        // property format. Keep the legacy date-property format checks too for
        // older Bags values, but prefer the current property keys.
        if (ContainsPropertyKey(value, ShellItemPropertyFormat, 12))
        {
            mode = DesktopIconSortMode.Size;
            return true;
        }
        if (ContainsPropertyKey(value, ShellItemPropertyFormat, 4))
        {
            mode = DesktopIconSortMode.Type;
            return true;
        }
        if (ContainsPropertyKey(value, ShellItemPropertyFormat, 15))
        {
            mode = DesktopIconSortMode.Created;
            return true;
        }
        if (ContainsPropertyKey(value, ShellItemPropertyFormat, 14) ||
            ContainsPropertyKey(value, ShellDatePropertyFormat, 14))
        {
            mode = DesktopIconSortMode.Modified;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Decodes IShellFolderViewDual3.SortColumns, for example
    /// <c>prop:-System.DateModified;</c>. A leading minus denotes descending
    /// order and is not represented in Explorer's persisted Sort value.
    /// </summary>
    public static DesktopIconSortState DecodeDesktopSortColumns(string? sortColumns) =>
        DecodeDesktopSortColumns(
            sortColumns,
            new DesktopIconSortState(DesktopIconSortMode.Name, false));

    public static DesktopIconSortState DecodeDesktopSortColumns(
        string? sortColumns,
        DesktopIconSortState fallback) =>
        TryDecodeDesktopSortColumns(sortColumns, out var state) ? state : fallback;

    private static bool TryDecodeDesktopSortColumns(
        string? sortColumns,
        out DesktopIconSortState state)
    {
        state = default;
        var token = sortColumns?
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        if (token.StartsWith("prop:", StringComparison.OrdinalIgnoreCase))
        {
            token = token[5..];
        }
        var descending = token.StartsWith("-", StringComparison.Ordinal);
        token = token.TrimStart('-');
        var mode = token.ToLowerInvariant() switch
        {
            "system.itemnamedisplay" or "system.name" => DesktopIconSortMode.Name,
            "system.size" => DesktopIconSortMode.Size,
            "system.itemtype" or "system.itemtypetext" => DesktopIconSortMode.Type,
            "system.datemodified" => DesktopIconSortMode.Modified,
            "system.datecreated" => DesktopIconSortMode.Created,
            _ => (DesktopIconSortMode?)null
        };
        if (mode is not { } decodedMode)
        {
            return false;
        }

        state = new DesktopIconSortState(decodedMode, descending);
        return true;
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

        // Explorer applies the new icon size and grid spacing asynchronously,
        // so the cached view must not answer for the state it just left.
        InvalidateCachedDesktopView();
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
    /// Returns the last icon spacing explicitly published for this ListView.
    /// Rendering never sends a synchronous message into Explorer; startup and
    /// explicit icon-zoom synchronization publish fresh values instead.
    /// </summary>
    public static bool TryGetCachedItemSpacing(IntPtr listView, out System.Drawing.Size spacing)
    {
        lock (ShellReadCacheGate)
        {
            if (_cachedSpacingListView == listView)
            {
                spacing = _cachedSpacing;
                return _cachedSpacingValid;
            }
        }
        spacing = default;
        return false;
    }

    /// <summary>
    /// Reads the live icon grid spacing without replacing a transient Shell
    /// timeout with a different layout. Callers that render an existing icon
    /// surface can retain their last valid spacing when this returns false.
    /// </summary>
    public static bool TryGetItemSpacing(IntPtr listView, out System.Drawing.Size spacing)
    {
        var read = ReadItemSpacing(listView, out spacing);
        lock (ShellReadCacheGate)
        {
            _cachedSpacingListView = listView;
            _cachedSpacing = spacing;
            _cachedSpacingValid = read;
        }
        return read;
    }

    private static bool ReadItemSpacing(IntPtr listView, out System.Drawing.Size spacing)
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
