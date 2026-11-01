using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CrabDesk.Native;

/// <summary>Retains desktop keyboard routing after a no-activate inline editor closes.</summary>
public static class DesktopInputProxy
{
    private const string Property = "CrabDesk.DesktopInputProxy";

    public static void Register(IntPtr hwnd)
    {
        if (!SetProp(hwnd, Property, new IntPtr(1))) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static void Unregister(IntPtr hwnd) => RemoveProp(hwnd, Property);

    public static bool IsHiddenEditor(IntPtr hwnd) => IsEditor(hwnd) && !NativeMethods.IsWindowVisible(hwnd);

    public static bool IsEditor(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        return processId == Environment.ProcessId && GetProp(hwnd, Property) != IntPtr.Zero;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetProp(IntPtr hwnd, string name, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetProp(IntPtr hwnd, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr RemoveProp(IntPtr hwnd, string name);
}
