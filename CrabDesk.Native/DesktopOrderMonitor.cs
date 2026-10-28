using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CrabDesk.Native;

/// <summary>Delivers desktop activation and ordering notifications on the registering UI thread.</summary>
public sealed class DesktopOrderMonitor : IDisposable
{
    private readonly IntPtr _desktopRoot;
    private readonly Action _changed;
    private readonly WinEventProc _callback;
    private GCHandle _callbackHandle;
    private IntPtr _foregroundHook, _reorderHook;
    private bool _disposed;

    public DesktopOrderMonitor(IntPtr desktopView, Action changed)
    {
        _desktopRoot = NativeMethods.GetAncestor(desktopView, 2);
        _changed = changed;
        _callback = OnWinEvent;
        _callbackHandle = GCHandle.Alloc(_callback);
        // OUTOFCONTEXT callbacks run on this thread's message loop. Keep them
        // synchronous there instead of adding a BeginInvoke or timer delay.
        _foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, _callback, 0, 0, 0);
        _reorderHook = SetWinEventHook(0x8004, 0x8004, IntPtr.Zero, _callback, 0, 0, 0);
        if (_foregroundHook == IntPtr.Zero || _reorderHook == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            Dispose();
            throw new Win32Exception(error);
        }
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint threadId, uint time)
    {
        if (_disposed || hwnd == IntPtr.Zero || childId != 0) return;
        if (eventType == 3 && hwnd == _desktopRoot ||
            eventType == 0x8004 && objectId is 0 or -4 &&
            (hwnd == _desktopRoot || hwnd == GetDesktopWindow()))
            _changed();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
        if (_reorderHook != IntPtr.Zero) UnhookWinEvent(_reorderHook);
        _foregroundHook = _reorderHook = IntPtr.Zero;
        if (_callbackHandle.IsAllocated) _callbackHandle.Free();
    }

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint threadId, uint time);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback,
        uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();
}
