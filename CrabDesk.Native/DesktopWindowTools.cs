using CrabDesk.Core;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CrabDesk.Native;

public static class DesktopWindowTools
{
    private const long WsExNoActivate = 0x08000000L;

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
