using CrabDesk.Core;
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
        style &= ~0x00CF0000L;
        style |= NativeMethods.WsChild | NativeMethods.WsVisible | NativeMethods.WsClipSiblings;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlStyle, new IntPtr(style));

        var extendedStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
        extendedStyle &= ~NativeMethods.WsExTransparent;
        extendedStyle |= NativeMethods.WsExToolWindow | WsExNoActivate;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlExStyle, new IntPtr(extendedStyle));
        NativeMethods.SetParent(hwnd, desktopParent);
    }

    public static void PositionAboveDesktop(IntPtr hwnd, IntPtr desktopView, int x, int y, int width, int height)
    {
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HwndTop,
            x,
            y,
            Math.Max(1, width),
            Math.Max(1, height),
            NativeMethods.SwpNoActivate | NativeMethods.SwpNoOwnerZOrder | NativeMethods.SwpShowWindow);
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
