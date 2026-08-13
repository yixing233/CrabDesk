using System.Runtime.InteropServices;
using System.Text;
using CrabDesk.Core;

namespace CrabDesk.Native;

public sealed class DesktopInputMonitor : IDesktopInputMonitor
{
    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMouseWheel = 0x020A;
    private const uint MnGetHMenu = 0x01E1;
    private const uint MfByPosition = 0x0400;
    private const uint ExplorerRefreshCommandId = 0x7003;
    private const uint MenuCommandTimeoutMilliseconds = 50;
    private const long DesktopContextMenuTrackingWindowMilliseconds = 10_000;
    private const int VkControl = 0x11;
    private readonly LowLevelMouseProc _callback;
    private IntPtr _hook;
    private long _desktopContextMenuExpiresAt;
    private bool _disposed;

    public DesktopInputMonitor()
    {
        _callback = MouseHook;
        _hook = SetWindowsHookEx(WhMouseLl, _callback, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            throw new InvalidOperationException("无法监听桌面双击操作。");
        }
    }

    public event EventHandler<DesktopIconZoomEventArgs>? IconZoomRequested;
    public event EventHandler? DesktopSurfaceClicked;
    public event EventHandler? DesktopContextMenuRequested;
    public event EventHandler? DesktopContextMenuCommandRequested;
    public event EventHandler? DesktopContextMenuRefreshRequested;

    public IntPtr DesktopListView { get; set; }
    public bool Enabled { get; set; }

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
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
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
                    if (IsCurrentProcessWindow(targetWindow))
                    {
                        DesktopIconPositionService.ForwardControlMouseWheel(
                            DesktopListView,
                            mouse.Point.X,
                            mouse.Point.Y,
                            delta);
                    }
                    IconZoomRequested?.Invoke(this, new DesktopIconZoomEventArgs(delta));
                }
            }
        }
        return CallNextHookEx(_hook, code, message, data);
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
        var window = WindowFromPoint(screenPoint);
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

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr message, IntPtr data);

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


    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc callback, IntPtr module, uint threadId);

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
