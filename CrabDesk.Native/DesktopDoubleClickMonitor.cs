using System.Runtime.InteropServices;
using System.Text;
using CrabDesk.Core;

namespace CrabDesk.Native;

public sealed class DesktopDoubleClickMonitor : IDesktopDoubleClickMonitor
{
    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201;
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

    public event EventHandler? EmptyAreaDoubleClicked;

    public IntPtr DesktopListView { get; set; }
    public bool Enabled { get; set; }

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
        if (code >= 0 && message.ToInt32() == WmLButtonDown && Enabled && DesktopListView != IntPtr.Zero)
        {
            var mouse = Marshal.PtrToStructure<LowLevelMouseHookStruct>(data);
            var elapsed = mouse.Time - _lastClickTime;
            var withinTime = _lastClickTime != 0 && elapsed <= GetDoubleClickTime();
            var withinDistance = Math.Abs(mouse.Point.X - _lastClickPoint.X) <= GetSystemMetrics(SmCxDoubleClick) / 2 &&
                Math.Abs(mouse.Point.Y - _lastClickPoint.Y) <= GetSystemMetrics(SmCyDoubleClick) / 2;
            _lastClickTime = mouse.Time;
            _lastClickPoint = mouse.Point;
            if (withinTime && withinDistance && IsEmptyDesktopPoint(mouse.Point))
            {
                _lastClickTime = 0;
                EmptyAreaDoubleClicked?.Invoke(this, EventArgs.Empty);
            }
        }
        return CallNextHookEx(_hook, code, message, data);
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
