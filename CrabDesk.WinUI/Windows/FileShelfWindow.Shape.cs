using System.Runtime.InteropServices;
using CrabDesk.Core;
using Microsoft.UI.Xaml;

namespace CrabDesk.WinUI.Windows;

public sealed partial class FileShelfWindow
{
    private (int X, int Y, int Width, int Height, int Radius, int DockRadius, FileShelfDockEdge Edge)? _shape;

    private void UpdateShape()
    {
        if (_closed) return;
        var bounds = _surfaceBounds;
        var width = (int)bounds.Width; var height = (int)bounds.Height;
        if (width <= 0 || height <= 0) return;
        var left = (int)(bounds.X - WindowBounds.X); var top = (int)(bounds.Y - WindowBounds.Y);
        var right = left + width; var bottom = top + height;
        var progress = _transition.Progress;
        var edge = progress < 1 ? _edge : FileShelfDockEdge.None;
        var radiusDip = 8 + 4 * progress;
        var dockRadiusDip = radiusDip * progress;
        var corners = edge switch
        {
            FileShelfDockEdge.Left => new CornerRadius(dockRadiusDip, radiusDip, radiusDip, dockRadiusDip),
            FileShelfDockEdge.Right => new CornerRadius(radiusDip, dockRadiusDip, dockRadiusDip, radiusDip),
            FileShelfDockEdge.Top => new CornerRadius(dockRadiusDip, dockRadiusDip, radiusDip, radiusDip),
            FileShelfDockEdge.Bottom => new CornerRadius(radiusDip, radiusDip, dockRadiusDip, dockRadiusDip),
            _ => new CornerRadius(radiusDip)
        };
        _shell.CornerRadius = corners;
        _shell.BorderThickness = new Thickness(progress);
        var radius = (int)Math.Round(Math.Min(radiusDip * DpiScale, Math.Min(width, height) / 2d));
        var dockRadius = Math.Min(radius, (int)Math.Round(dockRadiusDip * DpiScale));
        var shape = (left, top, width, height, radius, dockRadius, edge);
        if (_shape == shape) return;
        // Use the same outline for the HWND and XAML. The opaque frame fills the
        // antialiased fringe; transparent XAML corners must not expose a black HWND.
        if (_shape is null)
        {
            var noRounding = 1; var noBorder = unchecked((int)0xFFFFFFFE);
            DwmSetWindowAttribute(_hwnd, 33, ref noRounding, sizeof(int));
            DwmSetWindowAttribute(_hwnd, 34, ref noBorder, sizeof(int));
            var excludeFromPeek = 1;
            DwmSetWindowAttribute(_hwnd, 12, ref excludeFromPeek, sizeof(int));
        }
        var region = CreateRoundRectRgn(left, top, right + 1, bottom + 1, radius * 2, radius * 2);
        if (region == IntPtr.Zero) return;
        if (edge != FileShelfDockEdge.None)
        {
            var seam = edge switch
            {
                FileShelfDockEdge.Left => CreateRoundRectRgn(left, top, left + radius + dockRadius + 1, bottom + 1, dockRadius * 2, dockRadius * 2),
                FileShelfDockEdge.Right => CreateRoundRectRgn(right - radius - dockRadius, top, right + 1, bottom + 1, dockRadius * 2, dockRadius * 2),
                FileShelfDockEdge.Top => CreateRoundRectRgn(left, top, right + 1, top + radius + dockRadius + 1, dockRadius * 2, dockRadius * 2),
                _ => CreateRoundRectRgn(left, bottom - radius - dockRadius, right + 1, bottom + 1, dockRadius * 2, dockRadius * 2)
            };
            if (seam != IntPtr.Zero) { CombineRgn(region, region, seam, 2); DeleteObject(seam); }
        }
        var host = CreateRectRgn(0, 0, AppWindow.Size.Width, AppWindow.Size.Height);
        if (host != IntPtr.Zero) { CombineRgn(region, region, host, 1); DeleteObject(host); }
        if (SetWindowRgn(_hwnd, region, false) != 0) _shape = shape;
        else DeleteObject(region);
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr destination, IntPtr source1, IntPtr source2, int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
}
