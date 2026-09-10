using System.Runtime.InteropServices;

namespace CrabDesk.Native;

/// <summary>Desktop ordering for the top-level acrylic host; its children retain their input routing.</summary>
public static class DesktopAcrylicWindowTools
{
    private const string HostProperty = "CrabDesk.AcrylicDesktopHost";

    public static void Initialize(IntPtr hwnd, IntPtr desktopView)
    {
        var enabled = 1;
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(hwnd, 17, ref enabled, sizeof(int)));
        var margins = new Margins(-1, -1, -1, -1);
        Marshal.ThrowExceptionForHR(DwmExtendFrameIntoClientArea(hwnd, ref margins));
        if (!SetProp(hwnd, HostProperty, new IntPtr(1)))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        // Ownership preserves a top-level HWND (unlike SetParent) and associates
        // the tool window with the desktop for Show Desktop and task switching.
        NativeMethods.SetWindowLongPtr(hwnd, -8, NativeMethods.GetAncestor(desktopView, 2));
    }

    public static bool IsAcrylicSurface(IntPtr hwnd)
    {
        var root = NativeMethods.GetAncestor(hwnd, 2);
        NativeMethods.GetWindowThreadProcessId(root, out var processId);
        return processId == Environment.ProcessId && GetProp(root, HostProperty) != IntPtr.Zero;
    }

    public static void Release(IntPtr hwnd) => RemoveProp(hwnd, HostProperty);

    public static bool IsReadyAtDesktop(IntPtr hwnd, IntPtr desktopView)
    {
        return TryGetDesktopRoot(hwnd, desktopView, out var desktopRoot) &&
            NativeMethods.IsWindowVisible(hwnd) &&
            !IsIconic(hwnd) &&
            NativeMethods.GetWindow(desktopRoot, 3) == hwnd; // GW_HWNDPREV
    }

    public static bool PlaceAtDesktop(IntPtr hwnd, IntPtr desktopView)
    {
        if (!TryGetDesktopRoot(hwnd, desktopView, out var desktopRoot)) return false;

        // SWP_SHOWWINDOW does not restore an iconic window. SW_SHOWNOACTIVATE
        // restores its previous bounds without taking focus from an application.
        if (IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, NativeMethods.SwShowNoActivate);

        // Insert immediately above the desktop, below every ordinary app.
        // Never use HWND_TOP/TOPMOST here, including on a foreground change.
        var previous = NativeMethods.GetWindow(desktopRoot, 3); // GW_HWNDPREV
        var alreadyOrdered = previous == hwnd;
        if (alreadyOrdered && IsReadyAtDesktop(hwnd, desktopView)) return true;

        // STARTUPINFO can suppress the first Form.Show(). Ordering alone does
        // not reveal the host, and its layered children stay invisible too.
        if (!NativeMethods.SetWindowPos(hwnd, alreadyOrdered ? IntPtr.Zero : previous, 0, 0, 0, 0,
            NativeMethods.SwpNoActivate | NativeMethods.SwpNoMove |
            NativeMethods.SwpNoSize | NativeMethods.SwpNoOwnerZOrder |
            NativeMethods.SwpShowWindow | (alreadyOrdered ? (uint)NativeMethods.SwpNoZOrder : 0u)))
            return false;

        return IsReadyAtDesktop(hwnd, desktopView);
    }

    private static bool TryGetDesktopRoot(IntPtr hwnd, IntPtr desktopView, out IntPtr desktopRoot)
    {
        desktopRoot = IntPtr.Zero;
        if (!NativeMethods.IsWindow(hwnd) || !NativeMethods.IsWindow(desktopView) ||
            NativeMethods.GetAncestor(hwnd, 2) != hwnd)
            return false;

        desktopRoot = NativeMethods.GetAncestor(desktopView, 2);
        return desktopRoot != IntPtr.Zero && desktopRoot != hwnd && NativeMethods.IsWindow(desktopRoot);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Margins(int Left, int Right, int Top, int Bottom);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProp(IntPtr hwnd, string name, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetProp(IntPtr hwnd, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr RemoveProp(IntPtr hwnd, string name);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);
}
