using System.Runtime.InteropServices;
using System.Text;
using CrabDesk.Core;

namespace CrabDesk.Native;

public sealed class DesktopInputMonitor : IDesktopInputMonitor
{
    private const int WhMouseLl = 14;
    private const int WhKeyboardLl = 13;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMouseWheel = 0x020A;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const uint MnGetHMenu = 0x01E1;
    private const uint MfByPosition = 0x0400;
    private const uint ExplorerRefreshCommandId = 0x7003;
    private const uint MenuCommandTimeoutMilliseconds = 50;
    private const long DesktopContextMenuTrackingWindowMilliseconds = 10_000;
    private const int VkControl = 0x11;
    private const int VkReturn = 0x0D;
    private const int VkDelete = 0x2E;
    private const int VkA = 0x41;
    private const int VkC = 0x43;
    private const int VkV = 0x56;
    private const int VkX = 0x58;
    private const int VkF2 = 0x71;
    private readonly LowLevelHookProc _mouseCallback;
    private readonly LowLevelHookProc _keyboardCallback;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private long _desktopContextMenuExpiresAt;
    private readonly HashSet<uint> _interceptedKeyboardKeys = [];
    private bool _disposed;

    public DesktopInputMonitor()
    {
        _mouseCallback = MouseHook;
        _keyboardCallback = KeyboardHook;
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseCallback, GetModuleHandle(null), 0);
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardCallback, GetModuleHandle(null), 0);
        if (_mouseHook == IntPtr.Zero || _keyboardHook == IntPtr.Zero)
        {
            Dispose();
            throw new InvalidOperationException("无法监听桌面双击操作。");
        }
    }

    public event EventHandler<DesktopIconZoomEventArgs>? IconZoomRequested;
    public event EventHandler? DesktopSurfaceClicked;
    public event EventHandler? DesktopContextMenuRequested;
    public event EventHandler? DesktopContextMenuCommandRequested;
    public event EventHandler? DesktopContextMenuRefreshRequested;
    public event EventHandler? DesktopDeleteRequested;
    public event EventHandler? DesktopRenameRequested;
    public event EventHandler<DesktopKeyboardCommandEventArgs>? DesktopKeyboardCommandRequested;

    public IntPtr DesktopListView { get; set; }
    public bool Enabled { get; set; }
    public Func<int, int, bool>? IsPointerOverBox { get; set; }
    public Func<bool>? CanDeleteDesktopItems { get; set; }
    public Func<bool>? CanRenameDesktopItems { get; set; }
    public Func<DesktopKeyboardCommand, bool>? CanHandleDesktopKeyboardCommand { get; set; }

    /// <summary>
    /// Arms command tracking when the replacement icon layer forwards a
    /// right-click to Explorer itself. In that case the initial mouse message
    /// targets CrabDesk rather than Explorer, so the low-level hook has no
    /// other opportunity to start its native-menu tracking window.
    /// </summary>
    public void TrackDesktopContextMenu()
    {
        _desktopContextMenuExpiresAt = Environment.TickCount64 +
            DesktopContextMenuTrackingWindowMilliseconds;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
    }

    private IntPtr MouseHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && Enabled && DesktopListView != IntPtr.Zero)
        {
            var mouse = Marshal.PtrToStructure<LowLevelMouseHookStruct>(data);
            var msg = message.ToInt32();
            var isDesktopSurface = IsDesktopSurfacePoint(mouse.Point);
            var targetWindow = WindowFromPoint(mouse.Point);
            if ((msg == WmLButtonDown || msg == WmRButtonDown) &&
                isDesktopSurface &&
                !IsCurrentProcessWindow(targetWindow))
            {
                DesktopSurfaceClicked?.Invoke(this, EventArgs.Empty);
                if (msg == WmRButtonDown)
                {
                    TrackDesktopContextMenu();
                    DesktopContextMenuRequested?.Invoke(this, EventArgs.Empty);
                }
            }
            else if (msg == WmLButtonDown && IsDesktopContextMenuActive())
            {
                var isRefresh = IsNativeRefreshMenuItem(targetWindow, mouse.Point);
                if (isRefresh)
                {
                    _desktopContextMenuExpiresAt = 0;
                    DesktopContextMenuRefreshRequested?.Invoke(this, EventArgs.Empty);
                }
                else if (IsNativeSortMenuItem(targetWindow, mouse.Point))
                {
                    _desktopContextMenuExpiresAt = 0;
                    DesktopContextMenuCommandRequested?.Invoke(this, EventArgs.Empty);
                }
                // "Sort by" itself is a submenu. Retain the tracking window
                // after that parent item is clicked so the following click on
                // Name, Size, Type, or Date modified can be recognized.
            }
            else if (msg == WmMouseWheel &&
                     GetAsyncKeyState(VkControl) < 0 &&
                     isDesktopSurface)
            {
                var delta = unchecked((short)(mouse.MouseData >> 16));
                if (delta != 0)
                {
                    // Ctrl+wheel over a box zooms the icons of that box instead
                    // of Explorer unassigned-icon layer. Keep forwarding to the
                    // native ListView only while the pointer is on the desktop.
                    var overBox = IsPointerOverBox?.Invoke(mouse.Point.X, mouse.Point.Y) == true;
                    if (IsCurrentProcessWindow(targetWindow) && !overBox)
                    {
                        DesktopIconPositionService.ForwardControlMouseWheel(
                            DesktopListView,
                            mouse.Point.X,
                            mouse.Point.Y,
                            delta);
                    }
                    IconZoomRequested?.Invoke(
                        this,
                        new DesktopIconZoomEventArgs(delta, mouse.Point.X, mouse.Point.Y));
                }
            }
        }
        return CallNextHookEx(_mouseHook, code, message, data);
    }

    private IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && Enabled && DesktopListView != IntPtr.Zero)
        {
            var keyboard = Marshal.PtrToStructure<LowLevelKeyboardHookStruct>(data);
            var msg = message.ToInt32();
            if (msg == WmKeyUp || msg == WmSysKeyUp)
            {
                if (_interceptedKeyboardKeys.Remove(keyboard.VirtualKeyCode))
                {
                    return new IntPtr(1);
                }
            }
            else if (msg == WmKeyDown || msg == WmSysKeyDown)
            {
                if (_interceptedKeyboardKeys.Contains(keyboard.VirtualKeyCode))
                {
                    return new IntPtr(1);
                }

                if (TryGetDesktopKeyboardCommand(keyboard.VirtualKeyCode, out var command) &&
                    IsDesktopForeground() &&
                    CanHandleDesktopCommand(command))
                {
                    _interceptedKeyboardKeys.Add(keyboard.VirtualKeyCode);
                    DesktopKeyboardCommandRequested?.Invoke(
                        this,
                        new DesktopKeyboardCommandEventArgs(command));
                    if (command == DesktopKeyboardCommand.Delete)
                    {
                        DesktopDeleteRequested?.Invoke(this, EventArgs.Empty);
                    }
                    else if (command == DesktopKeyboardCommand.Rename)
                    {
                        DesktopRenameRequested?.Invoke(this, EventArgs.Empty);
                    }
                    return new IntPtr(1);
                }
            }
        }

        return CallNextHookEx(_keyboardHook, code, message, data);
    }

    private bool TryGetDesktopKeyboardCommand(uint virtualKey, out DesktopKeyboardCommand command)
    {
        command = virtualKey switch
        {
            (uint)VkDelete => DesktopKeyboardCommand.Delete,
            (uint)VkF2 => DesktopKeyboardCommand.Rename,
            (uint)VkReturn => DesktopKeyboardCommand.Open,
            (uint)VkA when GetAsyncKeyState(VkControl) < 0 => DesktopKeyboardCommand.SelectAll,
            (uint)VkC when GetAsyncKeyState(VkControl) < 0 => DesktopKeyboardCommand.Copy,
            (uint)VkX when GetAsyncKeyState(VkControl) < 0 => DesktopKeyboardCommand.Cut,
            (uint)VkV when GetAsyncKeyState(VkControl) < 0 => DesktopKeyboardCommand.Paste,
            _ => default
        };
        return virtualKey == (uint)VkDelete ||
            virtualKey == (uint)VkF2 ||
            virtualKey == (uint)VkReturn ||
            ((virtualKey == (uint)VkA ||
              virtualKey == (uint)VkC ||
              virtualKey == (uint)VkX ||
              virtualKey == (uint)VkV) && GetAsyncKeyState(VkControl) < 0);
    }

    private bool CanHandleDesktopCommand(DesktopKeyboardCommand command)
    {
        if (CanHandleDesktopKeyboardCommand is not null)
        {
            return CanHandleDesktopKeyboardCommand(command);
        }

        return command switch
        {
            DesktopKeyboardCommand.Delete => CanDeleteDesktopItems?.Invoke() == true,
            DesktopKeyboardCommand.Rename => CanRenameDesktopItems?.Invoke() == true,
            _ => false
        };
    }

    private bool IsDesktopContextMenuActive()
    {
        var expiresAt = _desktopContextMenuExpiresAt;
        if (expiresAt <= Environment.TickCount64)
        {
            _desktopContextMenuExpiresAt = 0;
            return false;
        }
        return true;
    }

    private static bool IsNativeRefreshMenuItem(IntPtr targetWindow, NativePoint point)
    {
        var menuWindow = GetAncestor(targetWindow, 2);
        if (menuWindow == IntPtr.Zero)
        {
            menuWindow = targetWindow;
        }
        if (!IsNativeMenuWindow(menuWindow) ||
            NativeMethods.SendMessageTimeout(
                menuWindow,
                MnGetHMenu,
                IntPtr.Zero,
                IntPtr.Zero,
                NativeMethods.SmtoAbortIfHung,
                MenuCommandTimeoutMilliseconds,
                out var menu) == IntPtr.Zero ||
            menu == IntPtr.Zero)
        {
            return false;
        }

        var itemPosition = MenuItemFromPoint(menuWindow, menu, point);
        if (itemPosition < 0)
        {
            return false;
        }

        if (GetMenuItemID(menu, itemPosition) == ExplorerRefreshCommandId)
        {
            return true;
        }

        var text = new StringBuilder(256);
        return GetMenuString(menu, (uint)itemPosition, text, text.Capacity, MfByPosition) > 0 &&
            (text.ToString().Contains("Refresh", StringComparison.OrdinalIgnoreCase) ||
             text.ToString().Contains("\u5237\u65b0", StringComparison.Ordinal));
    }

    private static bool IsNativeSortMenuItem(IntPtr targetWindow, NativePoint point)
    {
        var menuWindow = GetAncestor(targetWindow, 2);
        if (menuWindow == IntPtr.Zero)
        {
            menuWindow = targetWindow;
        }
        if (!IsNativeMenuWindow(menuWindow) ||
            NativeMethods.SendMessageTimeout(
                menuWindow,
                MnGetHMenu,
                IntPtr.Zero,
                IntPtr.Zero,
                NativeMethods.SmtoAbortIfHung,
                MenuCommandTimeoutMilliseconds,
                out var menu) == IntPtr.Zero ||
            menu == IntPtr.Zero)
        {
            return false;
        }
        var itemPosition = MenuItemFromPoint(menuWindow, menu, point);
        if (itemPosition < 0)
        {
            return false;
        }

        var text = new StringBuilder(256);
        if (GetMenuString(menu, (uint)itemPosition, text, text.Capacity, MfByPosition) <= 0)
        {
            return false;
        }

        var label = text.ToString();
        return label.Contains("Name", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("Size", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("Type", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("Modified", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("名称", StringComparison.Ordinal) ||
            label.Contains("大小", StringComparison.Ordinal) ||
            label.Contains("类型", StringComparison.Ordinal) ||
            label.Contains("日期", StringComparison.Ordinal) ||
            label.Contains("修改", StringComparison.Ordinal);
    }

    private static bool IsNativeMenuWindow(IntPtr window)
    {
        var className = new StringBuilder(16);
        GetClassName(window, className, className.Capacity);
        return string.Equals(className.ToString(), "#32768", StringComparison.Ordinal);
    }

    private bool IsDesktopSurfacePoint(NativePoint screenPoint)
    {
        return IsDesktopSurfaceWindow(WindowFromPoint(screenPoint));
    }

    private bool IsDesktopForeground()
    {
        var window = GetForegroundWindow();
        return window != IntPtr.Zero &&
            !IsCurrentProcessWindow(window) &&
            IsDesktopSurfaceWindow(window);
    }

    private bool IsDesktopSurfaceWindow(IntPtr window)
    {
        if (window == DesktopListView ||
            IsChild(DesktopListView, window) ||
            IsChild(window, DesktopListView) ||
            IsDesktopBackgroundWindow(window))
        {
            return true;
        }

        var desktopView = GetParent(DesktopListView);
        return desktopView != IntPtr.Zero &&
            (window == desktopView || IsChild(desktopView, window));
    }

    private static bool IsDesktopBackgroundWindow(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var processId);
        if (processId == Environment.ProcessId)
        {
            return false;
        }
        var root = GetAncestor(window, 2);
        var className = new StringBuilder(64);
        GetClassName(root == IntPtr.Zero ? window : root, className, className.Capacity);
        return className.ToString() is "WorkerW" or "Progman";
    }

    private static bool IsCurrentProcessWindow(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var processId);
        return processId == Environment.ProcessId;
    }

    private delegate IntPtr LowLevelHookProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelMouseHookStruct
    {
        internal NativePoint Point;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelKeyboardHookStruct
    {
        internal uint VirtualKeyCode;
        internal uint ScanCode;
        internal uint Flags;
        internal uint Time;
        internal IntPtr ExtraInfo;
    }


    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelHookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr window);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int capacity);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parent, IntPtr child);

    [DllImport("user32.dll")]
    private static extern int MenuItemFromPoint(IntPtr window, IntPtr menu, NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetMenuItemID(IntPtr menu, int position);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuString(
        IntPtr menu,
        uint item,
        StringBuilder text,
        int maxCount,
        uint flags);

}
