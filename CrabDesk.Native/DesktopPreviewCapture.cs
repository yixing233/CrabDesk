using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CrabDesk.Native;

/// <summary>
/// Captures the Explorer desktop host for a backup preview without inspecting
/// or changing the desktop ListView. The owner settings window is excluded
/// because only the desktop host is rendered.
/// </summary>
public static class DesktopPreviewCapture
{
    private const uint PwRenderFullContent = 0x00000002;
    private const int MaximumPreviewEdge = 1280;

    public static byte[]? TryCapturePng(IntPtr desktopParent)
    {
        if (desktopParent == IntPtr.Zero || !NativeMethods.IsWindow(desktopParent))
        {
            return null;
        }

        try
        {
            var bounds = DesktopWindowTools.GetWindowBounds(desktopParent);
            var width = Math.Max(1, (int)Math.Ceiling(bounds.Width));
            var height = Math.Max(1, (int)Math.Ceiling(bounds.Height));
            using var source = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(source))
            {
                var deviceContext = graphics.GetHdc();
                try
                {
                    if (!PrintWindow(desktopParent, deviceContext, PwRenderFullContent))
                    {
                        return null;
                    }
                }
                finally
                {
                    graphics.ReleaseHdc(deviceContext);
                }
            }

            var scale = Math.Min(1d, MaximumPreviewEdge / (double)Math.Max(width, height));
            var previewWidth = Math.Max(1, (int)Math.Round(width * scale));
            var previewHeight = Math.Max(1, (int)Math.Round(height * scale));
            using var preview = new Bitmap(previewWidth, previewHeight, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(preview))
            {
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(source, new Rectangle(0, 0, previewWidth, previewHeight));
            }

            using var stream = new MemoryStream();
            preview.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        catch (ExternalException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);
}
