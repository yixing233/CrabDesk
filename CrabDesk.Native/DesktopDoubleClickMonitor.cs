using System.Runtime.InteropServices;
using System.Text;
using CrabDesk.Core;

namespace CrabDesk.Native;

public sealed class DesktopDoubleClickMonitor : IDesktopDoubleClickMonitor
{
    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201;
    private const int WmMouseWheel = 0x020A;
    private const int VkControl = 0x11;
    private const int SmCxDoubleClick = 36;
    private const int SmCyDoubleClick = 37;
    private readonly LowLevelMouseProc _callback;
    private IntPtr _hook;
    private uint _lastClickTime;
    private NativePoint _lastClickPoint;
    private bool _disposed;

    public DesktopDoubleClickMonitor()
    {
        _callback = MouseHook;
        _hook = SetWindowsHookEx(WhMouseLl, _callback, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            throw new InvalidOperationException("无法监听桌面双击操作。");
        }
    }

    public event EventHandler? EmptyAreaClicked;
    public event EventHandler? EmptyAreaDoubleClicked;
    public event EventHandler<DesktopIconZoomEventArgs>? IconZoomRequested;

    public IntPtr DesktopListView { get; set; }
    public bool Enabled { get; set; }
    public bool DoubleClickEnabled { get; set; }

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
            if (message.ToInt32() == WmLButtonDown)
            {
                var elapsed = mouse.Time - _lastClickTime;
                var withinTime = _lastClickTime != 0 && elapsed <= GetDoubleClickTime();
                var withinDistance = Math.Abs(mouse.Point.X - _lastClickPoint.X) <= GetSystemMetrics(SmCxDoubleClick) / 2 &&
                    Math.Abs(mouse.Point.Y - _lastClickPoint.Y) <= GetSystemMetrics(SmCyDoubleClick) / 2;
                _lastClickTime = mouse.Time;
                _lastClickPoint = mouse.Point;
                if (IsEmptyDesktopPoint(mouse.Point))
                {
                    EmptyAreaClicked?.Invoke(this, EventArgs.Empty);
                    if (DoubleClickEnabled && withinTime && withinDistance)
                    {
                        _lastClickTime = 0;
                        EmptyAreaDoubleClicked?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
            else if (message.ToInt32() == WmMouseWheel &&
                     GetAsyncKeyState(VkControl) < 0 &&
                     IsDesktopSurfacePoint(mouse.Point))
            {
                var delta = unchecked((short)(mouse.MouseData >> 16));
                if (delta != 0)
                {
                    var targetWindow = WindowFromPoint(mouse.Point);
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

    private bool IsEmptyDesktopPoint(NativePoint screenPoint)
    {
        var window = WindowFromPoint(screenPoint);
        if (window != DesktopListView &&
            !IsChild(DesktopListView, window) &&
            !IsChild(window, DesktopListView) &&
            !IsDesktopBackgroundWindow(window))
        {
            return false;
        }

        var clientPoint = screenPoint;
        if (!ScreenToClient(DesktopListView, ref clientPoint))
        {
            return false;
        }
        return DesktopIconPositionService.IsEmptyPoint(DesktopListView, clientPoint.X, clientPoint.Y);
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
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);

}
