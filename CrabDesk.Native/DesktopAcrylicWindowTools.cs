using System.Runtime.InteropServices;
using System.Collections.Concurrent;

namespace CrabDesk.Native;

/// <summary>Desktop ordering for the top-level acrylic host; its children retain their input routing.</summary>
public static class DesktopAcrylicWindowTools
{
    private const string HostProperty = "CrabDesk.AcrylicDesktopHost";
    private static readonly ConcurrentDictionary<IntPtr, DesktopTransition> Transitions = new();

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
        Transitions.TryAdd(hwnd, new(NativeMethods.GetAncestor(desktopView, 2)));
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

    public static void Release(IntPtr hwnd)
    {
        FinishShowDesktopTransition(hwnd, force: true);
        Transitions.TryRemove(hwnd, out _);
        RemoveProp(hwnd, HostProperty);
    }

    // Called before forwarding the user's native Show Desktop input. Explorer
    // and the acrylic host then move in one native owner-group transaction,
    // rather than presenting Explorer one frame before an async repair.
    internal static void PrepareForShowDesktop()
    {
        foreach (var (hwnd, transition) in Transitions)
        {
            lock (transition)
            {
                if (transition.ExpiresAt != 0 || !IsVisibleAndRestored(hwnd) ||
                    !NativeMethods.IsWindow(transition.DesktopRoot)) continue;
                transition.PreviousOwner = NativeMethods.GetWindow(hwnd, 4);
                transition.ReturningToDesktop = NativeMethods.GetForegroundWindow() != transition.DesktopRoot;
                transition.DesktopActivatedAt = 0;
                transition.ExpiresAt = Environment.TickCount64 + 1500;
                NativeMethods.SetWindowLongPtr(hwnd, -8, transition.DesktopRoot);
            }
        }
    }

    internal static void PrepareForPopupClose(IntPtr popup, IntPtr activationTarget)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var active = GetActiveWindow();
        foreach (var (hwnd, transition) in Transitions)
        {
            lock (transition)
            {
                if (activationTarget != IntPtr.Zero &&
                    NativeMethods.GetAncestor(activationTarget, 2) != transition.DesktopRoot) continue;
                // A popup deactivated by another app must leave that app's
                // focus and ordering alone. Active also covers local UI focus
                // while Windows is still handing off global foreground rights.
                if (foreground != popup && active != popup && foreground != transition.DesktopRoot) continue;
                if (transition.ExpiresAt != 0 || !IsVisibleAndRestored(hwnd) ||
                    !NativeMethods.IsWindow(transition.DesktopRoot)) continue;
                transition.PreviousOwner = NativeMethods.GetWindow(hwnd, 4);
                transition.ClosingPopup = popup;
                transition.ReturningToDesktop = true;
                transition.DesktopActivatedAt = foreground == transition.DesktopRoot ? Environment.TickCount64 : 0;
                transition.ExpiresAt = Environment.TickCount64 + 1500;
                // Windows now moves the desktop and its composition host in
                // the same transaction, before a compositor frame can expose it.
                NativeMethods.SetWindowLongPtr(hwnd, -8, transition.DesktopRoot);
            }
        }
    }

    public static void FinishShowDesktopTransition(IntPtr hwnd, bool force = false)
    {
        if (!Transitions.TryGetValue(hwnd, out var transition)) return;
        lock (transition)
        {
            if (transition.ExpiresAt == 0) return;
            var now = Environment.TickCount64;
            var foreground = NativeMethods.GetForegroundWindow();
            var ordinaryForeground = foreground != IntPtr.Zero && foreground != hwnd && foreground != transition.DesktopRoot &&
                DesktopHostService.GetWindowClass(foreground) is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
            if (foreground == transition.DesktopRoot && transition.DesktopActivatedAt == 0)
                transition.DesktopActivatedAt = now;
            // Foreground arrives before Explorer's final animated reorder. Keep
            // the group through that tail, but detach immediately when a normal
            // application takes focus. An abandoned command has a bounded expiry.
            var completed = transition.ReturningToDesktop
                ? transition.DesktopActivatedAt != 0 && (ordinaryForeground || now - transition.DesktopActivatedAt >= 200)
                : ordinaryForeground;
            if (transition.ClosingPopup != IntPtr.Zero && foreground != transition.ClosingPopup && ordinaryForeground)
                completed = true;
            if (!force && !completed && now < transition.ExpiresAt) return;
            if (NativeMethods.IsWindow(hwnd) && NativeMethods.GetWindow(hwnd, 4) == transition.DesktopRoot)
                NativeMethods.SetWindowLongPtr(hwnd, -8, transition.PreviousOwner);
            transition.ExpiresAt = 0;
            transition.ClosingPopup = IntPtr.Zero;
        }
    }

    private sealed class DesktopTransition(IntPtr desktopRoot)
    {
        internal readonly IntPtr DesktopRoot = desktopRoot;
        internal IntPtr PreviousOwner;
        internal IntPtr ClosingPopup;
        internal long ExpiresAt;
        internal long DesktopActivatedAt;
        internal bool ReturningToDesktop;
    }

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
            if (!topmost && (!NativeMethods.IsWindowVisible(previous) || IsIconic(previous) || !HasWindowArea(previous))) continue;
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
            if (NativeMethods.IsWindowVisible(previous) && !IsIconic(previous) && HasWindowArea(previous)) return previous;
        return IntPtr.Zero;
    }

    // Some system utilities expose a visible, zero-size HWND. It cannot cover
    // desktop content, and its process may reject insertion beside it (error 5).
    private static bool HasWindowArea(IntPtr hwnd) => NativeMethods.GetWindowRect(hwnd, out var bounds) &&
        bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;

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
    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();
}
