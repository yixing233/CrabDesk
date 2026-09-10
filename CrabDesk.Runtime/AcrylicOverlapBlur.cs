using System;
using System.Buffers;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CrabDesk.Runtime;

/// <summary>
/// Experimental inter-box backdrop: soften only already-painted boxes under
/// the next box. The current box is painted afterwards and stays sharp.
/// HostBackdropBrush continues to supply the live wallpaper/desktop backdrop.
/// </summary>
internal static class AcrylicOverlapBlur
{
    private const int SampleScale = 2;
    private const double BlurRadiusDip = 8;

    internal static bool Apply(
        Bitmap frame,
        RectangleF bounds,
        float cornerRadius,
        double scale,
        IReadOnlyList<RectangleF> behind)
    {
        if (!double.IsFinite(scale) || scale <= 0 || bounds.Width <= 1 || bounds.Height <= 1)
            return false;

        var overlap = RectangleF.Empty;
        foreach (var lower in behind)
        {
            var intersection = RectangleF.Intersect(bounds, lower);
            if (intersection.Width <= 0 || intersection.Height <= 0) continue;
            overlap = overlap.IsEmpty ? intersection : RectangleF.Union(overlap, intersection);
        }
        if (overlap.IsEmpty) return false;

        var radius = Math.Max(1, (int)Math.Round(BlurRadiusDip * scale / SampleScale));
        // Three box-filter passes approximate a Gaussian. Include their full
        // support outside the upper box so its edges do not smear a clipped
        // source; round to device pixels only after the DIP conversion.
        var halo = 3 * radius * SampleScale + SampleScale;
        var canvas = new Rectangle(Point.Empty, frame.Size);
        var affected = Rectangle.Intersect(canvas, ToPixels(bounds, scale));
        var overlapPixels = ToPixels(overlap, scale);
        overlapPixels.Inflate(halo, halo);
        affected.Intersect(overlapPixels);
        if (affected.Width <= 0 || affected.Height <= 0) return false;
        var sample = affected;
        sample.Inflate(halo, halo);
        sample.Intersect(canvas);

        using var reduced = DesktopLayerBitmapFactory.Create(
            Math.Max(1, (sample.Width + SampleScale - 1) / SampleScale),
            Math.Max(1, (sample.Height + SampleScale - 1) / SampleScale));
        using (var graphics = Graphics.FromImage(reduced))
        using (var attributes = new ImageAttributes())
        {
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(frame, new Rectangle(Point.Empty, reduced.Size),
                sample.X, sample.Y, sample.Width, sample.Height, GraphicsUnit.Pixel, attributes);
        }
        BlurPremultiplied(reduced, radius);

        using var clip = RoundedOutline(RectangleF.Inflate(bounds, -0.5f, -0.5f), cornerRadius, scale);
        using (var graphics = Graphics.FromImage(frame))
        using (var attributes = new ImageAttributes())
        {
            graphics.SetClip(clip);
            graphics.SetClip(affected, CombineMode.Intersect);
            // Replace the original lower pixels, rather than drawing another
            // translucent copy on top (which would leave sharp ghost text).
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            graphics.DrawImage(reduced, sample, 0, 0, reduced.Width, reduced.Height,
                GraphicsUnit.Pixel, attributes);
        }
        return true;
    }

    private static Rectangle ToPixels(RectangleF rectangle, double scale) => Rectangle.FromLTRB(
        (int)Math.Floor(rectangle.Left * scale), (int)Math.Floor(rectangle.Top * scale),
        (int)Math.Ceiling(rectangle.Right * scale), (int)Math.Ceiling(rectangle.Bottom * scale));

    private static GraphicsPath RoundedOutline(RectangleF bounds, float radius, double scale)
    {
        var path = new GraphicsPath();
        var diameter = Math.Max(0, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
        if (diameter <= 0)
            path.AddRectangle(bounds);
        else
        {
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
        }
        using var transform = new Matrix((float)scale, 0, 0, (float)scale, 0, 0);
        path.Transform(transform);
        return path;
    }

    private static void BlurPremultiplied(Bitmap bitmap, int radius)
    {
        var length = checked(bitmap.Width * bitmap.Height * 4);
        var first = ArrayPool<byte>.Shared.Rent(length);
        var second = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size),
                ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
            try
            {
                var rowBytes = bitmap.Width * 4;
                for (var y = 0; y < bitmap.Height; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, first, y * rowBytes, rowBytes);
                // Filter all four premultiplied channels together. Blurring RGB
                // alone creates dark fringes around translucent lower boxes.
                for (var pass = 0; pass < 3; pass++)
                {
                    Filter(first, second, bitmap.Width, bitmap.Height, radius, horizontal: true);
                    Filter(second, first, bitmap.Width, bitmap.Height, radius, horizontal: false);
                }
                for (var y = 0; y < bitmap.Height; y++)
                    Marshal.Copy(first, y * rowBytes, data.Scan0 + y * data.Stride, rowBytes);
            }
            finally { bitmap.UnlockBits(data); }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(first);
            ArrayPool<byte>.Shared.Return(second);
        }
    }

    private static void Filter(byte[] source, byte[] target, int width, int height, int radius, bool horizontal)
    {
        var lines = horizontal ? height : width;
        var count = horizontal ? width : height;
        var step = horizontal ? 4 : width * 4;
        var divisor = radius * 2 + 1;
        for (var line = 0; line < lines; line++)
        {
            var start = horizontal ? line * width * 4 : line * 4;
            for (var channel = 0; channel < 4; channel++)
            {
                var sum = 0;
                for (var offset = -radius; offset <= radius; offset++)
                    sum += source[start + Math.Clamp(offset, 0, count - 1) * step + channel];
                for (var position = 0; position < count; position++)
                {
                    target[start + position * step + channel] = (byte)((sum + divisor / 2) / divisor);
                    sum -= source[start + Math.Max(0, position - radius) * step + channel];
                    sum += source[start + Math.Min(count - 1, position + radius + 1) * step + channel];
                }
            }
        }
    }
}
