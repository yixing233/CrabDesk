using System.Runtime.InteropServices;

namespace CrabDesk.Native;

public sealed class ShellContextMenuSession : IDisposable
{
    private const uint CommandFirst = 1;
    private const uint CommandLast = 0x7fff;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCommand = 0x0100;
    private const uint CmicMaskUnicode = 0x00004000;
    private const int SwShowNormal = 1;
    private const int WmDrawItem = 0x002b;
    private const int WmMeasureItem = 0x002c;
    private const int WmInitMenuPopup = 0x0117;
    private const int WmMenuChar = 0x0120;

    private readonly List<IntPtr> _absoluteItemIds;
    private readonly IShellFolder _parentFolder;
    private readonly IContextMenu _contextMenu;
    private readonly IContextMenu2? _contextMenu2;
    private readonly IContextMenu3? _contextMenu3;
    private bool _disposed;

    private ShellContextMenuSession(
        List<IntPtr> absoluteItemIds,
        IShellFolder parentFolder,
        IContextMenu contextMenu)
    {
        _absoluteItemIds = absoluteItemIds;
        _parentFolder = parentFolder;
        _contextMenu = contextMenu;
        _contextMenu2 = contextMenu as IContextMenu2;
        _contextMenu3 = contextMenu as IContextMenu3;
    }

    public static ShellContextMenuSession? TryCreate(
        IEnumerable<string> parsingNames,
        IntPtr ownerWindow)
    {
        var names = parsingNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (names.Length == 0)
        {
            return null;
        }
        if (names.Length > 1)
        {
            var parents = names
                .Select(name => File.Exists(name) || Directory.Exists(name)
                    ? Path.GetDirectoryName(Path.GetFullPath(name))
                    : null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (parents.Length != 1 || string.IsNullOrWhiteSpace(parents[0]))
            {
                return null;
            }
        }

        var absoluteIds = new List<IntPtr>(names.Length);
        IShellFolder? parentFolder = null;
        IContextMenu? contextMenu = null;
        try
        {
            var relativeIds = new List<IntPtr>(names.Length);
            foreach (var name in names)
            {
                var result = SHParseDisplayName(name, IntPtr.Zero, out var absoluteId, 0, out _);
                if (result != 0 || absoluteId == IntPtr.Zero)
                {
                    return null;
                }
                absoluteIds.Add(absoluteId);

                var folderId = typeof(IShellFolder).GUID;
                result = SHBindToParent(
                    absoluteId,
                    ref folderId,
                    out var parentObject,
                    out var relativeId);
                if (result != 0 || parentObject is not IShellFolder candidate || relativeId == IntPtr.Zero)
                {
                    if (parentObject is not null && Marshal.IsComObject(parentObject))
                    {
                        Marshal.FinalReleaseComObject(parentObject);
                    }
                    return null;
                }

                if (parentFolder is null)
                {
                    parentFolder = candidate;
                }
                else
                {
                    Marshal.FinalReleaseComObject(candidate);
                }
                relativeIds.Add(relativeId);
            }

            if (parentFolder is null)
            {
                return null;
            }
            var contextMenuId = typeof(IContextMenu).GUID;
            var contextResult = parentFolder.GetUIObjectOf(
                ownerWindow,
                (uint)relativeIds.Count,
                relativeIds.ToArray(),
                ref contextMenuId,
                IntPtr.Zero,
                out var menuObject);
            if (contextResult != 0 || menuObject is not IContextMenu menu)
            {
                if (menuObject is not null && Marshal.IsComObject(menuObject))
                {
                    Marshal.FinalReleaseComObject(menuObject);
                }
                return null;
            }
            contextMenu = menu;
            return new ShellContextMenuSession(absoluteIds, parentFolder, contextMenu);
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            if (contextMenu is null)
            {
                if (parentFolder is not null && Marshal.IsComObject(parentFolder))
                {
                    Marshal.FinalReleaseComObject(parentFolder);
                }
                foreach (var absoluteId in absoluteIds)
                {
                    Marshal.FreeCoTaskMem(absoluteId);
                }
            }
        }
    }

    public void Show(IntPtr ownerWindow, int screenX, int screenY)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }
        try
        {
            var result = _contextMenu.QueryContextMenu(
                menu,
                0,
                CommandFirst,
                CommandLast,
                0);
            if (result < 0)
            {
                return;
            }
            var command = TrackPopupMenuEx(
                menu,
                TpmRightButton | TpmReturnCommand,
                screenX,
                screenY,
                ownerWindow,
                IntPtr.Zero);
            if (command < CommandFirst)
            {
                return;
            }

            var commandOffset = new IntPtr(command - CommandFirst);
            var invoke = new CommandInvokeInfo
            {
                Size = Marshal.SizeOf<CommandInvokeInfo>(),
                Mask = CmicMaskUnicode,
                Owner = ownerWindow,
                Verb = commandOffset,
                Show = SwShowNormal,
                VerbUnicode = commandOffset,
                InvokePoint = new NativePoint { X = screenX, Y = screenY }
            };
            _contextMenu.InvokeCommand(ref invoke);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public bool TryHandleMessage(int message, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (_disposed || message is not (WmDrawItem or WmMeasureItem or WmInitMenuPopup or WmMenuChar))
        {
            return false;
        }
        if (_contextMenu3 is not null)
        {
            return _contextMenu3.HandleMenuMsg2(
                (uint)message,
                wParam,
                lParam,
                out result) == 0;
        }
        return _contextMenu2 is not null &&
            _contextMenu2.HandleMenuMsg((uint)message, wParam, lParam) == 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (Marshal.IsComObject(_contextMenu))
        {
            Marshal.FinalReleaseComObject(_contextMenu);
        }
        if (Marshal.IsComObject(_parentFolder))
        {
            Marshal.FinalReleaseComObject(_parentFolder);
        }
        foreach (var absoluteId in _absoluteItemIds)
        {
            Marshal.FreeCoTaskMem(absoluteId);
        }
        _absoluteItemIds.Clear();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHParseDisplayName(
        string name,
        IntPtr bindingContext,
        out IntPtr itemId,
        uint attributesIn,
        out uint attributesOut);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHBindToParent(
        IntPtr itemId,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out object parent,
        out IntPtr childItemId);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(
        IntPtr menu,
        uint flags,
        int x,
        int y,
        IntPtr owner,
        IntPtr parameters);

    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr owner, IntPtr bindingContext, [MarshalAs(UnmanagedType.LPWStr)] string displayName, ref uint eaten, out IntPtr itemId, ref uint attributes);
        [PreserveSig] int EnumObjects(IntPtr owner, uint flags, out IntPtr enumItemIds);
        [PreserveSig] int BindToObject(IntPtr itemId, IntPtr bindingContext, ref Guid interfaceId, out IntPtr result);
        [PreserveSig] int BindToStorage(IntPtr itemId, IntPtr bindingContext, ref Guid interfaceId, out IntPtr result);
        [PreserveSig] int CompareIDs(IntPtr parameter, IntPtr first, IntPtr second);
        [PreserveSig] int CreateViewObject(IntPtr owner, ref Guid interfaceId, out IntPtr result);
        [PreserveSig] int GetAttributesOf(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] itemIds, ref uint attributes);
        [PreserveSig] int GetUIObjectOf(
            IntPtr owner,
            uint count,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] itemIds,
            ref Guid interfaceId,
            IntPtr reserved,
            [MarshalAs(UnmanagedType.Interface)] out object result);
    }

    [ComImport]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint indexMenu, uint commandFirst, uint commandLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CommandInvokeInfo commandInfo);
        [PreserveSig] int GetCommandString(UIntPtr commandOffset, uint flags, IntPtr reserved, IntPtr name, uint nameLength);
    }

    [ComImport]
    [Guid("000214F4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint indexMenu, uint commandFirst, uint commandLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CommandInvokeInfo commandInfo);
        [PreserveSig] int GetCommandString(UIntPtr commandOffset, uint flags, IntPtr reserved, IntPtr name, uint nameLength);
        [PreserveSig] int HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam);
    }

    [ComImport]
    [Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint indexMenu, uint commandFirst, uint commandLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CommandInvokeInfo commandInfo);
        [PreserveSig] int GetCommandString(UIntPtr commandOffset, uint flags, IntPtr reserved, IntPtr name, uint nameLength);
        [PreserveSig] int HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommandInvokeInfo
    {
        internal int Size;
        internal uint Mask;
        internal IntPtr Owner;
        internal IntPtr Verb;
        internal IntPtr Parameters;
        internal IntPtr Directory;
        internal int Show;
        internal uint HotKey;
        internal IntPtr Icon;
        internal IntPtr Title;
        internal IntPtr VerbUnicode;
        internal IntPtr ParametersUnicode;
        internal IntPtr DirectoryUnicode;
        internal IntPtr TitleUnicode;
        internal NativePoint InvokePoint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }
}
