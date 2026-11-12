using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CrabDesk.Native;

/// <summary>Protects desktop composition before a popup hides or destroys its HWND.</summary>
public static class DesktopPopupWindow
{
    private static readonly SubclassProc Callback = WindowProcedure;
    private static readonly UIntPtr SubclassId = new(0x43525050);

    public static void Track(IntPtr hwnd)
    {
        if (!SetWindowSubclass(hwnd, Callback, SubclassId, UIntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam,
        UIntPtr subclassId, UIntPtr data)
    {
        // During WM_ACTIVATE, GetForegroundWindow can still name the old
        // popup. Record the incoming focus target before framework handlers
        // close it synchronously from their Deactivate event.
        if (message == 0x0006)
        {
            var target = (wParam.ToInt64() & 0xFFFF) == 0 ? lParam : IntPtr.Zero;
            _ = SetWindowSubclass(hwnd, Callback, subclassId, unchecked((UIntPtr)(nuint)target));
        }
        // WINDOWPOSCHANGING precedes both the native hide and its focus return.
        // This covers Close(), Escape, deactivation and framework destruction.
        if (message == 0x0046 && lParam != IntPtr.Zero &&
            (Marshal.PtrToStructure<WindowPosition>(lParam).Flags & 0x0080) != 0)
            DesktopAcrylicWindowTools.PrepareForPopupClose(hwnd, unchecked((IntPtr)(nint)data.ToUInt64()));
        if (message == 0x0082) // WM_NCDESTROY: no callback may outlive the HWND.
            _ = RemoveWindowSubclass(hwnd, Callback, subclassId);
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public IntPtr Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc callback, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc callback, UIntPtr id);
    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
