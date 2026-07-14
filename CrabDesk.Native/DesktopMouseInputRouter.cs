using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CrabDesk.Native;

public sealed class DesktopMouseInputRouter : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDoubleClick = 0x0203;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmMouseWheel = 0x020A;
    private const int WmMouseLeave = 0x02A3;
    private const int MkLButton = 0x0001;
    private const int MkRButton = 0x0002;
    private const int MkShift = 0x0004;
    private const int MkControl = 0x0008;
    private const int MkMButton = 0x0010;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int SmCxDoubleClick = 36;
    private const int SmCyDoubleClick = 37;

    private readonly IntPtr[] _surfaces;
    private readonly HookProc _callback;
    private readonly IntPtr _probeRegion;
    private IntPtr _hook;
    private IntPtr _hoverSurface;
    private IntPtr _capturedSurface;
    private IntPtr _lastClickSurface;
    private NativePoint _lastClickPoint;
    private uint _lastClickTime;
    private int _mouseKeys;

    public DesktopMouseInputRouter(IEnumerable<IntPtr> surfaces)
    {
        _surfaces = surfaces.Where(surface => surface != IntPtr.Zero).Distinct().ToArray();
        _callback = OnMouseHook;
        _probeRegion = CreateRectRgn(0, 0, 0, 0);
        _hook = SetWindowsHookEx(WhMouseLl, _callback, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            DeleteObject(_probeRegion);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to monitor desktop-box mouse input.");
        }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        if (_probeRegion != IntPtr.Zero)
        {
            DeleteObject(_probeRegion);
        }
    }

    private IntPtr OnMouseHook(int code, IntPtr messageValue, IntPtr data)
    {
        if (code < 0)
        {
            return CallNextHookEx(_hook, code, messageValue, data);
        }

        var message = messageValue.ToInt32();
        if (message is not (WmMouseMove or WmLButtonDown or WmLButtonUp or WmRButtonDown or WmRButtonUp or
            WmMButtonDown or WmMButtonUp or WmMouseWheel))
        {
            return CallNextHookEx(_hook, code, messageValue, data);
        }

        var capture = GetCapture();
        if (capture != IntPtr.Zero && !_surfaces.Contains(capture))
        {
            _capturedSurface = IntPtr.Zero;
            return CallNextHookEx(_hook, code, messageValue, data);
        }

        var input = Marshal.PtrToStructure<LowLevelMouseInput>(data);
        var target = _capturedSurface != IntPtr.Zero && IsWindow(_capturedSurface)
            ? _capturedSurface
            : FindSurface(input.Point);

        if (target == IntPtr.Zero)
        {
            NotifyMouseLeave();
            return CallNextHookEx(_hook, code, messageValue, data);
        }

        if (_hoverSurface != target)
        {
            NotifyMouseLeave();
            _hoverSurface = target;
        }

        UpdateMouseKeys(message);
        var routedMessage = message == WmLButtonDown && IsDoubleClick(target, input)
            ? WmLButtonDoubleClick
            : message;
        var clientPoint = input.Point;
        if (!ScreenToClient(target, ref clientPoint))
        {
            return CallNextHookEx(_hook, code, messageValue, data);
        }

        var keyState = _mouseKeys | ReadModifierKeys();
        var wParam = routedMessage == WmMouseWheel
            ? new IntPtr(unchecked((int)((input.MouseData & 0xffff0000u) | (uint)(keyState & 0xffff))))
            : new IntPtr(keyState);
        var lParam = routedMessage == WmMouseWheel
            ? PackPoint(input.Point)
            : PackPoint(clientPoint);
        PostMessage(target, (uint)routedMessage, wParam, lParam);

        if (message is WmLButtonDown or WmRButtonDown or WmMButtonDown)
        {
            _capturedSurface = target;
        }
        else if (message is WmLButtonUp or WmRButtonUp or WmMButtonUp)
        {
            _capturedSurface = IntPtr.Zero;
        }

        return new IntPtr(1);
    }

    private IntPtr FindSurface(NativePoint screenPoint)
    {
        var hitWindow = WindowFromPoint(screenPoint);
        var hitRoot = hitWindow == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hitWindow, 2);
        foreach (var surface in _surfaces)
        {
            if (!IsWindow(surface) || !IsWindowVisible(surface) ||
                GetAncestor(surface, 2) != hitRoot ||
                !GetWindowRect(surface, out var bounds) ||
                screenPoint.X < bounds.Left || screenPoint.X >= bounds.Right ||
                screenPoint.Y < bounds.Top || screenPoint.Y >= bounds.Bottom)
            {
                continue;
            }

            SetRectRgn(_probeRegion, 0, 0, 0, 0);
            if (GetWindowRgn(surface, _probeRegion) > 0 &&
                PtInRegion(_probeRegion, screenPoint.X - bounds.Left, screenPoint.Y - bounds.Top))
            {
                return surface;
            }
        }
        return IntPtr.Zero;
    }

    private void NotifyMouseLeave()
    {
        if (_hoverSurface == IntPtr.Zero)
        {
            return;
        }
        if (IsWindow(_hoverSurface))
        {
            PostMessage(_hoverSurface, WmMouseLeave, IntPtr.Zero, IntPtr.Zero);
        }
        _hoverSurface = IntPtr.Zero;
    }

    private bool IsDoubleClick(IntPtr surface, LowLevelMouseInput input)
    {
        var elapsed = unchecked(input.Time - _lastClickTime);
        var isDoubleClick = _lastClickSurface == surface &&
            elapsed <= GetDoubleClickTime() &&
            Math.Abs(input.Point.X - _lastClickPoint.X) <= Math.Max(1, GetSystemMetrics(SmCxDoubleClick) / 2) &&
            Math.Abs(input.Point.Y - _lastClickPoint.Y) <= Math.Max(1, GetSystemMetrics(SmCyDoubleClick) / 2);
        _lastClickSurface = isDoubleClick ? IntPtr.Zero : surface;
        _lastClickPoint = input.Point;
        _lastClickTime = isDoubleClick ? 0 : input.Time;
        return isDoubleClick;
    }

    private void UpdateMouseKeys(int message)
    {
        switch (message)
        {
            case WmLButtonDown:
                _mouseKeys |= MkLButton;
                break;
            case WmLButtonUp:
                _mouseKeys &= ~MkLButton;
                break;
            case WmRButtonDown:
                _mouseKeys |= MkRButton;
                break;
            case WmRButtonUp:
                _mouseKeys &= ~MkRButton;
                break;
            case WmMButtonDown:
                _mouseKeys |= MkMButton;
                break;
            case WmMButtonUp:
                _mouseKeys &= ~MkMButton;
                break;
        }
    }

    private static int ReadModifierKeys()
    {
        var result = 0;
        if ((GetAsyncKeyState(VkShift) & 0x8000) != 0)
        {
            result |= MkShift;
        }
        if ((GetAsyncKeyState(VkControl) & 0x8000) != 0)
        {
            result |= MkControl;
        }
        return result;
    }

    private static IntPtr PackPoint(NativePoint point) => new(
        unchecked((int)(((uint)point.Y & 0xffffu) << 16 | ((uint)point.X & 0xffffu))));

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelMouseInput
    {
        internal NativePoint Point;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal UIntPtr ExtraInfo;
    }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hook, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetWindowRgn(IntPtr window, IntPtr region);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetCapture();

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool SetRectRgn(IntPtr region, int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool PtInRegion(IntPtr region, int x, int y);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr value);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
