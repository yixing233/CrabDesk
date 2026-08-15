using CrabDesk.Core;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CrabDesk.Native;

public static class DesktopWindowTools
{
    private const long WsExNoActivate = 0x08000000L;

    /// <summary>
    /// Routes a background right-click from a CrabDesk desktop child back to
    /// Explorer so the normal desktop context menu remains available while
    /// the replacement icon layer accepts blank-area drag selection.
    /// </summary>
    public static bool ShowDesktopContextMenu(IntPtr desktopListView, int screenX, int screenY)
    {
        if (desktopListView == IntPtr.Zero || !NativeMethods.IsWindow(desktopListView))
        {
            return false;
        }

        var coordinates = unchecked((uint)(ushort)(short)screenX) |
            (unchecked((uint)(ushort)(short)screenY) << 16);
        return NativeMethods.PostMessage(
            desktopListView,
            NativeMethods.WmContextMenu,
            desktopListView,
            new IntPtr(unchecked((int)coordinates)));
    }

    /// <summary>
    /// Restores the native desktop as the foreground target after a click on a
    /// no-activate CrabDesk child. This scopes keyboard actions such as F2 to
    /// the desktop rather than the previously active application.
    /// </summary>
    public static bool TryActivateDesktopInput(IntPtr desktopListView)
    {
        if (desktopListView == IntPtr.Zero || !NativeMethods.IsWindow(desktopListView))
        {
            return false;
        }

        var root = NativeMethods.GetAncestor(desktopListView, NativeMethods.GaRoot);
        return root != IntPtr.Zero && NativeMethods.SetForegroundWindow(root);
    }

    public static void ToggleDesktop()
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        var shell = shellType is null ? null : Activator.CreateInstance(shellType);
        if (shell is null)
        {
            throw new InvalidOperationException("Unable to connect to Windows Shell.");
        }
        try
        {
            shellType!.InvokeMember(
                "ToggleDesktop",
                BindingFlags.InvokeMethod,
                null,
                shell,
                null);
        }
        finally
        {
            if (Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    public static void AttachAsDesktopChild(IntPtr hwnd, IntPtr desktopParent)
    {
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlStyle).ToInt64();
        style &= ~(0x00CF0000L | NativeMethods.WsPopup | NativeMethods.WsDisabled |
            NativeMethods.WsClipSiblings | NativeMethods.WsVisible);
        style |= NativeMethods.WsChild;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlStyle, new IntPtr(style));

        var extendedStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
        // The window region limits input to CrabDesk content; transparent style switching can starve child input.
        extendedStyle &= ~NativeMethods.WsExTransparent;
        extendedStyle |= NativeMethods.WsExToolWindow | WsExNoActivate;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlExStyle, new IntPtr(extendedStyle));
        NativeMethods.SetParent(hwnd, desktopParent);
        NormalizeDesktopSurfaceStyles(hwnd);
    }

    public static void PositionAboveDesktop(IntPtr hwnd, IntPtr desktopView, int x, int y, int width, int height)
    {
        if (desktopView == IntPtr.Zero || NativeMethods.GetParent(hwnd) != NativeMethods.GetParent(desktopView))
        {
            throw new InvalidOperationException("The CrabDesk surface and Explorer desktop view are not siblings.");
        }

        if (!NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HwndTop,
            x,
            y,
                Math.Max(1, width),
                Math.Max(1, height),
                NativeMethods.SwpNoActivate | NativeMethods.SwpNoOwnerZOrder))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to position the CrabDesk surface.");
        }
        if (!IsWindowAbove(hwnd, desktopView))
        {
            throw new InvalidOperationException("The CrabDesk surface is not above the Explorer desktop view.");
        }
        NormalizeDesktopSurfaceStyles(hwnd);
    }

    /// <summary>
    /// Explorer can raise the desktop list view when its icon visibility is
    /// toggled. Restore the existing CrabDesk surface to the top of the
    /// desktop view without changing its bounds or activating it.
    /// </summary>
    public static bool RestoreAboveDesktop(IntPtr hwnd, IntPtr desktopView)
    {
        if (desktopView == IntPtr.Zero || NativeMethods.GetParent(hwnd) != NativeMethods.GetParent(desktopView))
        {
            return false;
        }

        if (!NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HwndTop,
                0,
                0,
                0,
                0,
                NativeMethods.SwpNoActivate |
                NativeMethods.SwpNoMove |
                NativeMethods.SwpNoSize |
                NativeMethods.SwpNoOwnerZOrder))
        {
            return false;
        }

        return IsWindowAbove(hwnd, desktopView);
    }

    /// <summary>
    /// Places a desktop child immediately above another child in the same
    /// Explorer desktop parent.  This explicit insertion point is important
    /// when the lower child is a full-monitor layered icon surface: HWND_TOP
    /// alone can leave a regular box child visually underneath it even when
    /// the reported sibling order looks correct.
    /// </summary>
    public static bool PlaceAbove(IntPtr hwnd, IntPtr below)
    {
        if (hwnd == IntPtr.Zero || below == IntPtr.Zero ||
            NativeMethods.GetParent(hwnd) != NativeMethods.GetParent(below))
        {
            return false;
        }

        return NativeMethods.SetWindowPos(
            hwnd,
            below,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoActivate |
            NativeMethods.SwpNoMove |
            NativeMethods.SwpNoSize);
    }

    /// <summary>
    /// Re-shows a desktop child after Explorer recreated its view, preserving
    /// the no-activate behavior while placing it above the icon ListView.
    /// </summary>
    public static bool ShowAboveDesktop(IntPtr hwnd, IntPtr desktopView)
    {
        if (desktopView == IntPtr.Zero || NativeMethods.GetParent(hwnd) != NativeMethods.GetParent(desktopView))
        {
            return false;
        }

        if (!NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HwndTop,
                0,
                0,
                0,
                0,
                NativeMethods.SwpNoActivate |
                NativeMethods.SwpNoMove |
                NativeMethods.SwpNoSize |
                NativeMethods.SwpNoOwnerZOrder |
                NativeMethods.SwpShowWindow))
        {
            return false;
        }

        return IsDesktopSurfaceReady(hwnd, desktopView);
    }

    public static bool IsWindowAbove(IntPtr hwnd, IntPtr other)
    {
        var parent = NativeMethods.GetParent(hwnd);
        if (parent == IntPtr.Zero || parent != NativeMethods.GetParent(other))
        {
            return false;
        }

        for (var current = NativeMethods.GetTopWindow(parent);
             current != IntPtr.Zero;
             current = NativeMethods.GetWindow(current, NativeMethods.GwHwndNext))
        {
            if (current == hwnd)
            {
                return true;
            }
            if (current == other)
            {
                return false;
            }
        }
        return false;
    }

    public static bool IsDesktopSurfaceReady(IntPtr hwnd, IntPtr desktopView)
    {
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlStyle).ToInt64();
        var extendedStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
        return NativeMethods.IsWindowVisible(hwnd) &&
            NativeMethods.IsWindowEnabled(hwnd) &&
            (style & NativeMethods.WsChild) != 0 &&
            (style & NativeMethods.WsClipSiblings) == 0 &&
            (extendedStyle & NativeMethods.WsExTransparent) == 0 &&
            IsWindowAbove(hwnd, desktopView);
    }

    private static void NormalizeDesktopSurfaceStyles(IntPtr hwnd)
    {
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlStyle).ToInt64();
        var expectedStyle = (style & ~(NativeMethods.WsPopup | NativeMethods.WsDisabled | NativeMethods.WsClipSiblings)) |
            NativeMethods.WsChild;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlStyle, new IntPtr(expectedStyle));

        var extendedStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
        var expectedExtendedStyle = (extendedStyle & ~NativeMethods.WsExTransparent) |
            NativeMethods.WsExToolWindow | WsExNoActivate;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlExStyle, new IntPtr(expectedExtendedStyle));
        NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoActivate |
            NativeMethods.SwpNoMove |
            NativeMethods.SwpNoSize |
            NativeMethods.SwpNoZOrder |
            NativeMethods.SwpFrameChanged);
    }

    public static long GetSurfaceExtendedStyle(IntPtr hwnd) =>
        NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();

    /// <summary>
    /// Hides Explorer's complete desktop icon view only after CrabDesk's
    /// replacement surface has rendered successfully. No filesystem metadata
    /// or individual ListView item is changed.
    /// </summary>
    public static bool TryHideDesktopIconView(IntPtr desktopListView, out bool wasVisible)
    {
        wasVisible = false;
        if (desktopListView == IntPtr.Zero || !NativeMethods.IsWindow(desktopListView))
        {
            return false;
        }

        wasVisible = NativeMethods.IsWindowVisible(desktopListView);
        if (!wasVisible)
        {
            return true;
        }

        NativeMethods.ShowWindow(desktopListView, NativeMethods.SwHide);
        return !NativeMethods.IsWindowVisible(desktopListView);
    }

    /// <summary>
    /// Repairs a hidden Explorer ListView left behind when a previous visual
    /// desktop process ended before it could restore the native icon layer.
    /// Callers decide whether the user's shell preference says icons should
    /// be visible; this method only restores the window itself and never
    /// changes that preference.
    /// </summary>
    public static bool EnsureDesktopIconViewVisible(IntPtr desktopListView)
    {
        if (desktopListView == IntPtr.Zero || !NativeMethods.IsWindow(desktopListView))
        {
            return false;
        }

        if (NativeMethods.IsWindowVisible(desktopListView))
        {
            return true;
        }

        NativeMethods.ShowWindow(desktopListView, NativeMethods.SwShowNoActivate);
        return NativeMethods.IsWindowVisible(desktopListView);
    }

    public static void RestoreDesktopIconView(IntPtr desktopListView, bool wasVisible)
    {
        if (!wasVisible || desktopListView == IntPtr.Zero || !NativeMethods.IsWindow(desktopListView))
        {
            return;
        }

        NativeMethods.ShowWindow(desktopListView, NativeMethods.SwShowNoActivate);
    }

    // RichEdit persists SelectionBackColor as character formatting. Select
    // the document, restore automatic foreground/background colors, then put
    // the caret back where it was so stale highlights never remain visible.
    public static void ResetRichEditTextFormatting(IntPtr richEditHandle, int caretStart = 0)
    {
        if (richEditHandle == IntPtr.Zero)
        {
            return;
        }

        var format = new NativeMethods.CharFormat2
        {
            CbSize = (uint)Marshal.SizeOf<NativeMethods.CharFormat2>(),
            DwMask = NativeMethods.CfmColor | NativeMethods.CfmBackColor,
            DwEffects = NativeMethods.CfeAutoColor | NativeMethods.CfeAutoBackColor,
            CrTextColor = 0,
            CrBackColor = 0
        };
        NativeMethods.SendMessage(richEditHandle, NativeMethods.EmSetSel, IntPtr.Zero, new IntPtr(-1));
        NativeMethods.SendMessage(
            richEditHandle,
            NativeMethods.EmSetCharFormat,
            (IntPtr)NativeMethods.ScfSelection,
            ref format);
        NativeMethods.SendMessage(
            richEditHandle,
            NativeMethods.EmSetSel,
            new IntPtr(caretStart),
            new IntPtr(caretStart));
    }

    public static string GetDesktopSurfaceDiagnostics(IntPtr hwnd, IntPtr desktopView)
    {
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlStyle).ToInt64();
        var extendedStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
        return $"visible={NativeMethods.IsWindowVisible(hwnd)} enabled={NativeMethods.IsWindowEnabled(hwnd)} " +
            $"style=0x{style:X} ex=0x{extendedStyle:X} child={(style & NativeMethods.WsChild) != 0} " +
            $"clipSiblings={(style & NativeMethods.WsClipSiblings) != 0} " +
            $"transparent={(extendedStyle & NativeMethods.WsExTransparent) != 0} " +
            $"above={IsWindowAbove(hwnd, desktopView)} parent=0x{NativeMethods.GetParent(hwnd).ToInt64():X} " +
            $"viewParent=0x{NativeMethods.GetParent(desktopView).ToInt64():X}";
    }

    public static void PositionBehindWindow(IntPtr hwnd, IntPtr windowInFront, int x, int y, int width, int height)
    {
        NativeMethods.SetWindowPos(
            hwnd,
            windowInFront,
            x,
            y,
            Math.Max(1, width),
            Math.Max(1, height),
            NativeMethods.SwpNoActivate | NativeMethods.SwpNoOwnerZOrder | NativeMethods.SwpShowWindow);
    }

    public static bool ApplyRegion(
        IntPtr hwnd,
        IEnumerable<LayoutRect> rectangles,
        double scale,
        out string diagnostic,
        bool redraw = true)
    {
        var deviceRectangles = ToDeviceRectangles(rectangles, scale).ToArray();
        var destination = NativeMethods.CreateRectRgn(0, 0, 0, 0);
        if (destination == IntPtr.Zero)
        {
            diagnostic = $"CreateRectRgn failed error={Marshal.GetLastWin32Error()}";
            return false;
        }
        try
        {
            foreach (var rectangle in deviceRectangles)
            {
                var source = NativeMethods.CreateRectRgn(
                    rectangle.Left,
                    rectangle.Top,
                    rectangle.Right,
                    rectangle.Bottom);
                if (source == IntPtr.Zero)
                {
                    diagnostic = $"CreateRectRgn failed error={Marshal.GetLastWin32Error()}";
                    return false;
                }
                try
                {
                    if (NativeMethods.CombineRgn(destination, destination, source, NativeMethods.RgnOr) == NativeMethods.Error)
                    {
                        diagnostic = $"CombineRgn failed error={Marshal.GetLastWin32Error()}";
                        return false;
                    }
                }
                finally
                {
                    NativeMethods.DeleteObject(source);
                }
            }

            if (NativeMethods.SetWindowRgn(hwnd, destination, redraw) == 0)
            {
                diagnostic = $"SetWindowRgn failed error={Marshal.GetLastWin32Error()}";
                return false;
            }
            destination = IntPtr.Zero;
            return VerifyRegion(hwnd, deviceRectangles, out diagnostic);
        }
        finally
        {
            if (destination != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(destination);
            }
        }
    }

    /// <summary>
    /// Applies a union of rounded rectangles as the native window region.
    /// This is used by the regular desktop box child so WinForms' backing
    /// paint cannot leak a square background through the visual box corners.
    /// </summary>
    public static bool ApplyRoundedRegion(
        IntPtr hwnd,
        IEnumerable<LayoutRect> rectangles,
        double scale,
        double cornerRadius,
        out string diagnostic,
        bool redraw = true)
    {
        var deviceRectangles = ToDeviceRectangles(rectangles, scale).ToArray();
        var destination = NativeMethods.CreateRectRgn(0, 0, 0, 0);
        if (destination == IntPtr.Zero)
        {
            diagnostic = $"CreateRectRgn failed error={Marshal.GetLastWin32Error()}";
            return false;
        }
        try
        {
            var diameter = Math.Max(1, (int)Math.Round(cornerRadius * scale * 2));
            foreach (var rectangle in deviceRectangles)
            {
                var source = NativeMethods.CreateRoundRectRgn(
                    rectangle.Left,
                    rectangle.Top,
                    rectangle.Right,
                    rectangle.Bottom,
                    diameter,
                    diameter);
                if (source == IntPtr.Zero)
                {
                    diagnostic = $"CreateRoundRectRgn failed error={Marshal.GetLastWin32Error()}";
                    return false;
                }
                try
                {
                    if (NativeMethods.CombineRgn(destination, destination, source, NativeMethods.RgnOr) == NativeMethods.Error)
                    {
                        diagnostic = $"CombineRgn failed error={Marshal.GetLastWin32Error()}";
                        return false;
                    }
                }
                finally
                {
                    NativeMethods.DeleteObject(source);
                }
            }

            if (NativeMethods.SetWindowRgn(hwnd, destination, redraw) == 0)
            {
                diagnostic = $"SetWindowRgn failed error={Marshal.GetLastWin32Error()}";
                return false;
            }
            destination = IntPtr.Zero;
            return VerifyRoundedRegion(hwnd, deviceRectangles, out diagnostic);
        }
        finally
        {
            if (destination != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(destination);
            }
        }
    }

    public static bool VerifyRoundedRegion(
        IntPtr hwnd,
        IEnumerable<LayoutRect> rectangles,
        double scale,
        out string diagnostic) =>
        VerifyRoundedRegion(hwnd, ToDeviceRectangles(rectangles, scale).ToArray(), out diagnostic);

    private static bool VerifyRoundedRegion(
        IntPtr hwnd,
        IReadOnlyList<NativeMethods.Rect> deviceRectangles,
        out string diagnostic)
    {
            // CreateRoundRectRgn omits the extreme right/bottom corner pixel,
            // so GetWindowRgnBox reports those edges one pixel inward.  That
            // is valid rounded geometry rather than a lost box region.
            var regionType = NativeMethods.GetWindowRgnBox(hwnd, out var actualBounds);
            if (regionType == NativeMethods.Error)
            {
                diagnostic = $"GetWindowRgnBox failed error={Marshal.GetLastWin32Error()}";
                return false;
            }
            if (deviceRectangles.Count == 0)
            {
                diagnostic = regionType == NativeMethods.NullRegion
                    ? "region=NULL"
                    : $"Expected an empty region but got type={regionType} bounds={FormatRect(actualBounds)}";
                return regionType == NativeMethods.NullRegion;
            }
            var expectedBounds = GetBoundingRect(deviceRectangles);
            var valid = regionType is NativeMethods.SimpleRegion or NativeMethods.ComplexRegion &&
                actualBounds.Left == expectedBounds.Left &&
                actualBounds.Top == expectedBounds.Top &&
                actualBounds.Right >= expectedBounds.Right - 1 &&
                actualBounds.Right <= expectedBounds.Right &&
                actualBounds.Bottom >= expectedBounds.Bottom - 1 &&
                actualBounds.Bottom <= expectedBounds.Bottom;
            diagnostic = $"roundedRegion type={regionType} bounds={FormatRect(actualBounds)} " +
                         $"expected={FormatRect(expectedBounds)} valid={valid}";
            return valid;
    }

    public static bool ApplyRegionExcluding(
        IntPtr hwnd,
        IEnumerable<LayoutRect> rectangles,
        IEnumerable<LayoutRect> excludedRectangles,
        double scale,
        out string diagnostic,
        bool redraw = true)
    {
        var included = ToDeviceRectangles(rectangles, scale).ToArray();
        var excluded = ToDeviceRectangles(excludedRectangles, scale).ToArray();
        var destination = NativeMethods.CreateRectRgn(0, 0, 0, 0);
        if (destination == IntPtr.Zero)
        {
            diagnostic = $"CreateRectRgn failed error={Marshal.GetLastWin32Error()}";
            return false;
        }
        try
        {
            foreach (var rectangle in included)
            {
                var source = NativeMethods.CreateRectRgn(
                    rectangle.Left,
                    rectangle.Top,
                    rectangle.Right,
                    rectangle.Bottom);
                if (source == IntPtr.Zero)
                {
                    diagnostic = $"CreateRectRgn failed error={Marshal.GetLastWin32Error()}";
                    return false;
                }
                try
                {
                    if (NativeMethods.CombineRgn(destination, destination, source, NativeMethods.RgnOr) == NativeMethods.Error)
                    {
                        diagnostic = $"CombineRgn failed error={Marshal.GetLastWin32Error()}";
                        return false;
                    }
                }
                finally
                {
                    NativeMethods.DeleteObject(source);
                }
            }

            foreach (var rectangle in excluded)
            {
                var source = NativeMethods.CreateRectRgn(
                    rectangle.Left,
                    rectangle.Top,
                    rectangle.Right,
                    rectangle.Bottom);
                if (source == IntPtr.Zero)
                {
                    diagnostic = $"CreateRectRgn failed error={Marshal.GetLastWin32Error()}";
                    return false;
                }
                try
                {
                    NativeMethods.CombineRgn(destination, destination, source, NativeMethods.RgnDiff);
                }
                finally
                {
                    NativeMethods.DeleteObject(source);
                }
            }

            if (NativeMethods.SetWindowRgn(hwnd, destination, redraw) == 0)
            {
                diagnostic = $"SetWindowRgn failed error={Marshal.GetLastWin32Error()}";
                return false;
            }
            destination = IntPtr.Zero;
            if (included.Length == 0)
            {
                diagnostic = "region=NULL";
                return true;
            }
            var expectedBounds = GetBoundingRect(included);
            var regionType = NativeMethods.GetWindowRgnBox(hwnd, out var actualBounds);
            if (regionType == NativeMethods.Error)
            {
                diagnostic = $"GetWindowRgnBox failed error={Marshal.GetLastWin32Error()}";
                return false;
            }
            diagnostic = $"regionType={regionType} bounds={FormatRect(actualBounds)} " +
                         $"expectedBounds={FormatRect(expectedBounds)} excluded={excluded.Length}";
            return actualBounds.Left == expectedBounds.Left &&
                actualBounds.Top == expectedBounds.Top &&
                actualBounds.Right == expectedBounds.Right &&
                actualBounds.Bottom == expectedBounds.Bottom;
        }
        finally
        {
            if (destination != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(destination);
            }
        }
    }

    public static bool VerifyRegion(
        IntPtr hwnd,
        IEnumerable<LayoutRect> rectangles,
        double scale,
        out string diagnostic) =>
        VerifyRegion(hwnd, ToDeviceRectangles(rectangles, scale).ToArray(), out diagnostic);

    private static bool VerifyRegion(
        IntPtr hwnd,
        IReadOnlyList<NativeMethods.Rect> expectedRectangles,
        out string diagnostic)
    {
        var regionType = NativeMethods.GetWindowRgnBox(hwnd, out var actualBounds);
        if (regionType == NativeMethods.Error)
        {
            diagnostic = $"GetWindowRgnBox failed error={Marshal.GetLastWin32Error()}";
            return false;
        }

        if (expectedRectangles.Count == 0)
        {
            if (regionType == NativeMethods.NullRegion)
            {
                diagnostic = "region=NULL";
                return true;
            }

            diagnostic = $"Expected an empty region but got type={regionType} bounds={FormatRect(actualBounds)}";
            return false;
        }

        if (regionType is not (NativeMethods.SimpleRegion or NativeMethods.ComplexRegion))
        {
            diagnostic = $"Expected a non-empty region but got type={regionType} bounds={FormatRect(actualBounds)}";
            return false;
        }

        var expectedBounds = GetBoundingRect(expectedRectangles);
        if (actualBounds.Left != expectedBounds.Left ||
            actualBounds.Top != expectedBounds.Top ||
            actualBounds.Right != expectedBounds.Right ||
            actualBounds.Bottom != expectedBounds.Bottom)
        {
            diagnostic = $"Region bounds mismatch expected={FormatRect(expectedBounds)} actual={FormatRect(actualBounds)} type={regionType}";
            return false;
        }

        diagnostic = $"regionType={regionType} bounds={FormatRect(actualBounds)}";
        return true;
    }

    private static IntPtr CreateRegion(IEnumerable<LayoutRect> rectangles, double scale)
    {
        var destination = NativeMethods.CreateRectRgn(0, 0, 0, 0);
        if (destination == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }
        foreach (var rectangle in rectangles)
        {
            var source = NativeMethods.CreateRectRgn(
                (int)Math.Floor(rectangle.X * scale),
                (int)Math.Floor(rectangle.Y * scale),
                (int)Math.Ceiling((rectangle.X + rectangle.Width) * scale),
                (int)Math.Ceiling((rectangle.Y + rectangle.Height) * scale));
            if (source == IntPtr.Zero)
            {
                continue;
            }
            try
            {
                NativeMethods.CombineRgn(destination, destination, source, NativeMethods.RgnOr);
            }
            finally
            {
                NativeMethods.DeleteObject(source);
            }
        }
        return destination;
    }

    private static IEnumerable<NativeMethods.Rect> ToDeviceRectangles(
        IEnumerable<LayoutRect> rectangles,
        double scale)
    {
        foreach (var rectangle in rectangles)
        {
            var left = (int)Math.Floor(rectangle.X * scale);
            var top = (int)Math.Floor(rectangle.Y * scale);
            var right = (int)Math.Ceiling((rectangle.X + rectangle.Width) * scale);
            var bottom = (int)Math.Ceiling((rectangle.Y + rectangle.Height) * scale);
            if (right <= left || bottom <= top)
            {
                continue;
            }

            yield return new NativeMethods.Rect
            {
                Left = left,
                Top = top,
                Right = right,
                Bottom = bottom
            };
        }
    }

    private static NativeMethods.Rect GetBoundingRect(IReadOnlyList<NativeMethods.Rect> rectangles)
    {
        var bounds = rectangles[0];
        for (var index = 1; index < rectangles.Count; index++)
        {
            var rectangle = rectangles[index];
            bounds.Left = Math.Min(bounds.Left, rectangle.Left);
            bounds.Top = Math.Min(bounds.Top, rectangle.Top);
            bounds.Right = Math.Max(bounds.Right, rectangle.Right);
            bounds.Bottom = Math.Max(bounds.Bottom, rectangle.Bottom);
        }
        return bounds;
    }

    private static string FormatRect(NativeMethods.Rect rectangle) =>
        $"{rectangle.Left},{rectangle.Top},{rectangle.Right},{rectangle.Bottom}";

    public static LayoutRect GetWindowBounds(IntPtr hwnd)
    {
        return NativeMethods.GetWindowRect(hwnd, out var rect)
            ? new LayoutRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)
            : default;
    }
}
