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
            throw new InvalidOperationException("无法连接 Windows Shell。");
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
        style &= ~(0x00CF0000L | NativeMethods.WsPopup | NativeMethods.WsDisabled | NativeMethods.WsClipSiblings);
        style |= NativeMethods.WsChild | NativeMethods.WsVisible;
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
            NativeMethods.SwpNoActivate | NativeMethods.SwpNoOwnerZOrder | NativeMethods.SwpShowWindow))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to position the CrabDesk surface.");
        }
        if (!IsWindowAbove(hwnd, desktopView))
        {
            throw new InvalidOperationException("The CrabDesk surface is not above the Explorer desktop view.");
        }
        NormalizeDesktopSurfaceStyles(hwnd);
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

    public static void NormalizeDesktopSurfaceStyles(IntPtr hwnd)
    {
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlStyle).ToInt64();
        var expectedStyle = (style & ~(NativeMethods.WsPopup | NativeMethods.WsDisabled | NativeMethods.WsClipSiblings)) |
            NativeMethods.WsChild | NativeMethods.WsVisible;
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

        var actualStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlStyle).ToInt64();
        var actualExtendedStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
        if ((actualStyle & NativeMethods.WsClipSiblings) != 0 ||
            (actualExtendedStyle & NativeMethods.WsExTransparent) != 0)
        {
            throw new InvalidOperationException(
                $"Desktop surface styles did not persist. style=0x{actualStyle:X} ex=0x{actualExtendedStyle:X}");
        }
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

    public static void ApplyRegion(IntPtr hwnd, IEnumerable<LayoutRect> rectangles, double scale)
    {
        var destination = NativeMethods.CreateRectRgn(0, 0, 0, 0);
        try
        {
            foreach (var rectangle in rectangles)
            {
                var source = NativeMethods.CreateRectRgn(
                    (int)Math.Floor(rectangle.X * scale),
                    (int)Math.Floor(rectangle.Y * scale),
                    (int)Math.Ceiling((rectangle.X + rectangle.Width) * scale),
                    (int)Math.Ceiling((rectangle.Y + rectangle.Height) * scale));
                try
                {
                    NativeMethods.CombineRgn(destination, destination, source, NativeMethods.RgnOr);
                }
                finally
                {
                    NativeMethods.DeleteObject(source);
                }
            }

            if (NativeMethods.SetWindowRgn(hwnd, destination, true) != 0)
            {
                destination = IntPtr.Zero;
            }
        }
        finally
        {
            if (destination != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(destination);
            }
        }
    }

    public static LayoutRect GetWindowBounds(IntPtr hwnd)
    {
        return NativeMethods.GetWindowRect(hwnd, out var rect)
            ? new LayoutRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)
            : default;
    }
}
