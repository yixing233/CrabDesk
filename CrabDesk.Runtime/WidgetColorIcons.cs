using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CrabDesk.Runtime;

/// <summary>Cached raster derivatives of the shared transparent SVG assets.</summary>
public static class WidgetColorIcons
{
    private static readonly ConcurrentDictionary<string, Lazy<Bitmap>> Images = new(StringComparer.Ordinal);

    public static void Draw(Graphics graphics, string assetName, RectangleF bounds, float opacity = 1)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || opacity <= 0) return;
        var bitmap = Images.GetOrAdd(assetName, name => new(() => Load(name))).Value;
        var size = Math.Min(bounds.Width, bounds.Height);
        var target = new RectangleF(bounds.X + (bounds.Width - size) / 2, bounds.Y + (bounds.Height - size) / 2, size, size);
        var state = graphics.Save();
        try
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            if (opacity < 1)
                attributes.SetColorMatrix(new ColorMatrix { Matrix33 = opacity });
            lock (bitmap)
                graphics.DrawImage(bitmap, Rectangle.Round(target), 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel, attributes);
        }
        finally { graphics.Restore(state); }
    }

    private static Bitmap Load(string name)
    {
        var assembly = typeof(WidgetColorIcons).Assembly;
        using var stream = assembly.GetManifestResourceStream($"CrabDesk.Runtime.Assets.WidgetIcons.{name}.png")
            ?? throw new InvalidOperationException($"Missing widget icon: {name}");
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }
}
