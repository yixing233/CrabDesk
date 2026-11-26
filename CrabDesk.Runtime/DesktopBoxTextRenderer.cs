using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using Wpf = System.Windows;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;

namespace CrabDesk.Runtime;

/// <summary>Monitor-resolution grayscale text for transparent box surfaces.</summary>
internal sealed class DesktopBoxTextRenderer : IDisposable
{
    private const int MaximumEntries = 512;
    private const long MaximumBytes = 16 * 1024 * 1024;
    private readonly record struct Key(string Text, SizeF Size, string Family, float Points, FontStyle Style,
        StringAlignment Alignment, StringAlignment LineAlignment, StringTrimming Trimming,
        StringFormatFlags Flags, float ScaleX, float ScaleY, int Color);
    private sealed record Entry(Key Key, Bitmap Bitmap, float Top)
    {
        internal long Bytes => (long)Bitmap.Width * Bitmap.Height * 4;
    }
    private readonly Dictionary<Key, LinkedListNode<Entry>> _cache = [];
    private readonly LinkedList<Entry> _recent = new();
    private long _bytes;

    internal void Draw(Graphics graphics, string value, Font font, Color color, RectangleF field, StringFormat format)
    {
        if (string.IsNullOrEmpty(value) || field.Width <= 0 || field.Height <= 0) return;
        using var transform = graphics.Transform;
        var scaleX = transform.Elements[0];
        var scaleY = transform.Elements[3];
        if (!float.IsFinite(scaleX) || !float.IsFinite(scaleY) || scaleX <= 0 || scaleY <= 0) return;
        var key = new Key(value, field.Size, font.Name, font.SizeInPoints, font.Style,
            format.Alignment, format.LineAlignment, format.Trimming, format.FormatFlags, scaleX, scaleY, color.ToArgb());
        Entry entry;
        if (_cache.TryGetValue(key, out var node))
        {
            _recent.Remove(node);
            _recent.AddLast(node);
            entry = node.Value;
        }
        else
        {
            entry = Rasterize(key, font, format, color);
            while (_recent.First is { } oldest &&
                   (_cache.Count >= MaximumEntries || _bytes + entry.Bytes > MaximumBytes))
            {
                _bytes -= oldest.Value.Bytes;
                _cache.Remove(oldest.Value.Key);
                _recent.RemoveFirst();
                oldest.Value.Bitmap.Dispose();
            }
            if (entry.Bytes <= MaximumBytes)
            {
                _cache.Add(key, _recent.AddLast(entry));
                _bytes += entry.Bytes;
            }
        }

        var state = graphics.Save();
        try
        {
            graphics.SetClip(field, CombineMode.Intersect);
            PointF[] origin = [new(field.X, field.Y + entry.Top)];
            graphics.TransformPoints(CoordinateSpace.Device, CoordinateSpace.World, origin);
            graphics.ResetTransform();
            // Rasterize once at monitor DPI, then copy at whole physical pixels.
            // Scroll offsets must not filter the glyphs again on every frame.
            graphics.PixelOffsetMode = PixelOffsetMode.None;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.DrawImage(entry.Bitmap, new Rectangle(Point.Round(origin[0]), entry.Bitmap.Size),
                0, 0, entry.Bitmap.Width, entry.Bitmap.Height, GraphicsUnit.Pixel);
        }
        finally
        {
            graphics.Restore(state);
            if (entry.Bytes > MaximumBytes) entry.Bitmap.Dispose();
        }
    }

    internal static SizeF Measure(Graphics graphics, string value, Font font, SizeF field, StringFormat format)
    {
        if (string.IsNullOrEmpty(value) || field.Width <= 0 || field.Height <= 0) return SizeF.Empty;
        using var transform = graphics.Transform;
        var text = Format(value, font, field, format, Color.Black, Math.Max(0.01, transform.Elements[3]));
        return new((float)text.WidthIncludingTrailingWhitespace, (float)text.Height);
    }

    private static Media.FormattedText Format(string value, Font font, SizeF field, StringFormat format, Color color, double scale)
    {
        var text = new Media.FormattedText(value, CultureInfo.CurrentUICulture, Wpf.FlowDirection.LeftToRight,
            new Media.Typeface(new Media.FontFamily(font.Name), font.Italic ? Wpf.FontStyles.Italic : Wpf.FontStyles.Normal,
                font.Bold ? Wpf.FontWeights.Bold : Wpf.FontWeights.Normal, Wpf.FontStretches.Normal),
            font.SizeInPoints * 96d / 72d,
            new Media.SolidColorBrush(Media.Color.FromArgb(color.A, color.R, color.G, color.B)),
            null, Media.TextFormattingMode.Display, scale)
        {
            MaxTextWidth = field.Width,
            MaxTextHeight = field.Height,
            Trimming = format.Trimming == StringTrimming.None ? Wpf.TextTrimming.None : Wpf.TextTrimming.CharacterEllipsis,
            TextAlignment = format.Alignment switch
            {
                StringAlignment.Center => Wpf.TextAlignment.Center,
                StringAlignment.Far => Wpf.TextAlignment.Right,
                _ => Wpf.TextAlignment.Left
            }
        };
        if (format.FormatFlags.HasFlag(StringFormatFlags.NoWrap)) text.MaxLineCount = 1;
        else if (format.FormatFlags.HasFlag(StringFormatFlags.LineLimit) && format.Trimming != StringTrimming.None)
            text.MaxLineCount = 2;
        return text;
    }

    private static Entry Rasterize(Key key, Font font, StringFormat format, Color color)
    {
        var text = Format(key.Text, font, key.Size, format, color, key.ScaleY);
        var top = format.LineAlignment switch
        {
            StringAlignment.Center => (key.Size.Height - text.Height) / 2,
            StringAlignment.Far => key.Size.Height - text.Height,
            _ => 0
        };
        var width = Math.Max(1, (int)Math.Ceiling(key.Size.Width * key.ScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(Math.Min(key.Size.Height, text.Height + 2 / key.ScaleY) * key.ScaleY));
        var visual = new Media.DrawingVisual();
        Media.TextOptions.SetTextRenderingMode(visual, Media.TextRenderingMode.Grayscale);
        Media.TextOptions.SetTextFormattingMode(visual, Media.TextFormattingMode.Display);
        Media.TextOptions.SetTextHintingMode(visual, Media.TextHintingMode.Fixed);
        Media.VisualTreeHelper.SetRootDpi(visual, new Wpf.DpiScale(key.ScaleX, key.ScaleY));
        using (var drawing = visual.RenderOpen()) drawing.DrawText(text, new Wpf.Point(0, 0));
        var raster = new Imaging.RenderTargetBitmap(width, height, 96 * key.ScaleX, 96 * key.ScaleY, Media.PixelFormats.Pbgra32);
        raster.Render(visual);
        var bitmap = DesktopLayerBitmapFactory.Create(width, height);
        try
        {
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try { raster.CopyPixels(Wpf.Int32Rect.Empty, data.Scan0, data.Stride * height, data.Stride); }
            finally { bitmap.UnlockBits(data); }
        }
        catch { bitmap.Dispose(); throw; }
        return new(key, bitmap, (float)(Math.Round(top * key.ScaleY) / key.ScaleY));
    }

    public void Dispose()
    {
        foreach (var entry in _recent) entry.Bitmap.Dispose();
        _recent.Clear();
        _cache.Clear();
        _bytes = 0;
    }
}
