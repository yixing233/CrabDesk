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
        // Desktop content must stay painted during Peek. Otherwise DWM fades
        // this top-level host independently of its HWND visibility and Z-order.
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(hwnd, 12, ref enabled, sizeof(int))); // DWMWA_EXCLUDED_FROM_PEEK
        var margins = new Margins(-1, -1, -1, -1);
        Marshal.ThrowExceptionForHR(DwmExtendFrameIntoClientArea(hwnd, ref margins));
        if (!SetProp(hwnd, HostProperty, new IntPtr(1)))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        // Keep WinForms' private tool-window owner. Owning this top-level host
        // by Explorer lets popup/overlay reordering lift Explorer along with
        // the host, invalidating the desktop insertion anchor itself.
    }

    public static bool IsAcrylicSurface(IntPtr hwnd)
    {
        var root = NativeMethods.GetAncestor(hwnd, 2);
        NativeMethods.GetWindowThreadProcessId(root, out var processId);
        return processId == Environment.ProcessId && GetProp(root, HostProperty) != IntPtr.Zero;
    }

    public static void Release(IntPtr hwnd) => RemoveProp(hwnd, HostProperty);

    public static string GetPlacementDiagnostics(IntPtr hwnd, IntPtr desktopView)
    {
        var error = Marshal.GetLastWin32Error();
        var root = NativeMethods.GetAncestor(desktopView, 2);
        var previous = NativeMethods.GetWindow(root, 3);
        var next = NativeMethods.GetWindow(hwnd, 2);
        return $"error={error} host=0x{hwnd.ToInt64():X} valid={NativeMethods.IsWindow(hwnd)} " +
            $"visible={NativeMethods.IsWindowVisible(hwnd)} iconic={IsIconic(hwnd)} " +
            $"topmost={IsTopmost(hwnd)} " +
            $"hostRoot=0x{NativeMethods.GetAncestor(hwnd, 2).ToInt64():X} owner=0x{NativeMethods.GetWindow(hwnd, 4).ToInt64():X} " +
            $"desktopRoot=0x{root.ToInt64():X} previous=0x{previous.ToInt64():X} ({DesktopHostService.GetWindowClass(previous)}) " +
            $"hostNext=0x{next.ToInt64():X} ({DesktopHostService.GetWindowClass(next)})";
    }

    public static bool IsReadyAtDesktop(IntPtr hwnd, IntPtr desktopView)
    {
        return TryGetDesktopRoot(hwnd, desktopView, out var desktopRoot) &&
            IsVisibleAndRestored(hwnd) &&
            !IsTopmost(hwnd) &&
            GetVisibleWindowAbove(desktopRoot) == hwnd;
    }

    public static bool IsVisibleAndRestored(IntPtr hwnd) =>
        NativeMethods.IsWindowVisible(hwnd) && !IsIconic(hwnd);

    public static bool ConstrainPositionChange(IntPtr hwnd, IntPtr desktopView, IntPtr positionPointer)
    {
        if (positionPointer == IntPtr.Zero || !TryGetDesktopRoot(hwnd, desktopView, out var desktopRoot)) return false;
        var position = Marshal.PtrToStructure<WindowPosition>(positionPointer);
        if (position.Window != hwnd || (position.Flags & NativeMethods.SwpNoZOrder) != 0) return false;
        var requestedAfter = position.InsertAfter;
        if (IsReadyAtDesktop(hwnd, desktopView))
            position.Flags |= NativeMethods.SwpNoZOrder;
        else
            position.InsertAfter = GetPlacementAnchor(hwnd, desktopRoot);
        Marshal.StructureToPtr(position, positionPointer, false);
        return requestedAfter != position.InsertAfter ||
            ((position.Flags & NativeMethods.SwpNoZOrder) != 0 &&
             (requestedAfter == IntPtr.Zero || requestedAfter == new IntPtr(-1) || IsTopmost(requestedAfter)));
    }

    public static bool PlaceAtDesktop(IntPtr hwnd, IntPtr desktopView)
    {
        if (!TryGetDesktopRoot(hwnd, desktopView, out var desktopRoot)) return false;

        // Reveal/restore before resolving the insertion anchor: Explorer can
        // reorder its desktop and taskbar when this owned window first appears.
        // Using the pre-show neighbor would make the readiness check fail even
        // though SetWindowPos succeeded. Keep the foreground application active.
        if (IsIconic(hwnd) || !NativeMethods.IsWindowVisible(hwnd))
            NativeMethods.ShowWindow(hwnd, NativeMethods.SwShowNoActivate);

        // Explorer can reorder the taskbar/desktop while SetWindowPos reveals
        // an owned host too. If the resulting order is wrong, resolve it once
        // more; keep the host immediately above the desktop, below other apps.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (IsReadyAtDesktop(hwnd, desktopView)) return true;
            var previous = GetPlacementAnchor(hwnd, desktopRoot);
            if (!NativeMethods.SetWindowPos(hwnd, previous, 0, 0, 0, 0,
                NativeMethods.SwpNoActivate | NativeMethods.SwpNoMove |
                NativeMethods.SwpNoSize | NativeMethods.SwpNoOwnerZOrder |
                NativeMethods.SwpShowWindow))
                return false;
            if (IsReadyAtDesktop(hwnd, desktopView)) return true;
            if (!NativeMethods.IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
        }
        return false;
    }

    private static IntPtr GetPlacementAnchor(IntPtr hwnd, IntPtr desktopRoot)
    {
        for (var previous = NativeMethods.GetWindow(desktopRoot, 3); previous != IntPtr.Zero;
             previous = NativeMethods.GetWindow(previous, 3))
        {
            if (previous == hwnd) continue;
            // Even hidden topmost windows belong to the topmost band. Using
            // a visible window above them would temporarily promote the host.
            var topmost = IsTopmost(previous);
            if (!topmost && (!NativeMethods.IsWindowVisible(previous) || IsIconic(previous))) continue;
            var owner = NativeMethods.GetWindow(previous, 4);
            while (owner != IntPtr.Zero && owner != hwnd) owner = NativeMethods.GetWindow(owner, 4);
            if (owner == hwnd) continue;
            // A normal anchor clears topmost state and repairs the order in
            // one operation, without briefly raising the host above other apps.
            // A topmost boundary requires explicit demotion first.
            return IsTopmost(hwnd) && topmost ? new IntPtr(-2) : previous;
        }
        // Show Desktop can leave only the topmost taskbar above Explorer.
        // Use that actual boundary when present: HWND_TOP alone can be kept
        // behind the foreground desktop by Windows' foreground restrictions.
        // HWND_BOTTOM would also put this host behind Explorer.
        return IsTopmost(hwnd) ? new IntPtr(-2) : IntPtr.Zero;
    }

    private static bool IsTopmost(IntPtr hwnd) => hwnd != IntPtr.Zero &&
        (NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64() & 0x8) != 0;

    private static IntPtr GetVisibleWindowAbove(IntPtr hwnd)
    {
        // Hidden IME/utility windows are not stacking anchors. Some belong to
        // protected processes and reject SetWindowPos insertion with error 5.
        // They also cannot visually obscure a correctly placed desktop host.
        for (var previous = NativeMethods.GetWindow(hwnd, 3); previous != IntPtr.Zero;
             previous = NativeMethods.GetWindow(previous, 3)) // GW_HWNDPREV
            if (NativeMethods.IsWindowVisible(previous) && !IsIconic(previous)) return previous;
        return IntPtr.Zero;
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
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public IntPtr Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }
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
