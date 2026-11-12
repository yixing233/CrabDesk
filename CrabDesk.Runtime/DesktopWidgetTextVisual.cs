using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using CrabDesk.Core;
using Wpf = System.Windows;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;

namespace CrabDesk.Runtime;

/// <summary>Cached WPF text layout matching the desktop widget editors.</summary>
internal sealed class DesktopWidgetTextVisual : IDisposable
{
    private readonly record struct LayoutKey(string Text, int Width, int Height, double Scale, int Color,
        double FontSize, string FontFamily, int FontWeight, bool Italic, Wpf.Thickness Insets, bool SingleLine, bool Strike);
    private LayoutKey? _key;
    private Media.FormattedText? _text;
    private Bitmap? _bitmap;
    private float _offset;
    internal double Height => _text?.Height ?? 0;

    internal float Draw(Graphics graphics, string value, RectangleF field, Color color, float offset,
        double fontSize = 13, Wpf.Thickness? insets = null, bool singleLine = false, bool strike = false)
    {
        using var transform = graphics.Transform;
        var widget = WidgetTextRendering.Current(graphics);
        fontSize *= WidgetTypography.NormalizeScale(widget?.FontScale ?? 1);
        var family = WidgetTypography.NormalizeFamily(widget?.FontFamily);
        var weight = widget?.FontWeight ?? 0;
        var italic = widget?.FontItalic == true;
        var scale = (double)transform.Elements[0];
        var width = Math.Max(1, (int)Math.Round(field.Width * scale));
        var height = Math.Max(1, (int)Math.Round(field.Height * scale));
        var padding = insets ?? new Wpf.Thickness(10, 8, 10, 8);
        var key = new LayoutKey(value, width, height, scale, color.ToArgb(), fontSize, family, weight, italic, padding, singleLine, strike);
        var content = new Wpf.Rect(padding.Left, padding.Top, Math.Max(1, width / scale - padding.Left - padding.Right),
            Math.Max(1, height / scale - padding.Top - padding.Bottom));
        if (_key != key)
        {
            var ink = new Media.SolidColorBrush(Media.Color.FromArgb(color.A, color.R, color.G, color.B));
            _text = new Media.FormattedText(value, CultureInfo.CurrentUICulture, Wpf.FlowDirection.LeftToRight,
                new Media.Typeface(new Media.FontFamily(family), italic ? Wpf.FontStyles.Italic : Wpf.FontStyles.Normal,
                    weight == 700 ? Wpf.FontWeights.Bold : Wpf.FontWeights.Normal, Wpf.FontStretches.Normal),
                fontSize, ink, null, Media.TextFormattingMode.Display, scale)
                { MaxTextWidth = content.Width, Trimming = singleLine ? Wpf.TextTrimming.CharacterEllipsis : Wpf.TextTrimming.None };
            if (singleLine) _text.MaxLineCount = 1;
            if (strike) _text.SetTextDecorations(Wpf.TextDecorations.Strikethrough);
            _key = key;
            _bitmap?.Dispose(); _bitmap = null;
        }
        offset = (float)Math.Clamp(offset, 0, Math.Max(0, Height - content.Height));
        if (_bitmap is null || _offset != offset)
        {
            var visual = new Media.DrawingVisual();
            Media.TextOptions.SetTextRenderingMode(visual, Media.TextRenderingMode.Grayscale);
            Media.VisualTreeHelper.SetRootDpi(visual, new Wpf.DpiScale(scale, scale));
            using (var drawing = visual.RenderOpen())
            {
                drawing.PushClip(new Media.RectangleGeometry(content));
                var top = singleLine ? content.Y + (content.Height - Height) / 2 : content.Y - offset;
                drawing.DrawText(_text!, new Wpf.Point(content.X, top));
                drawing.Pop();
            }
            var raster = new Imaging.RenderTargetBitmap(width, height, 96 * scale, 96 * scale, Media.PixelFormats.Pbgra32);
            raster.Render(visual);
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try { raster.CopyPixels(Wpf.Int32Rect.Empty, data.Scan0, data.Stride * height, data.Stride); }
            finally { bitmap.UnlockBits(data); }
            _bitmap?.Dispose(); _bitmap = bitmap; _offset = offset;
        }
        var bounds = new RectangleF((float)(Math.Round(field.X * scale) / scale), (float)(Math.Round(field.Y * scale) / scale),
            (float)(width / scale), (float)(height / scale));
        graphics.DrawImage(_bitmap, bounds, new RectangleF(0, 0, width, height), GraphicsUnit.Pixel);
        return offset;
    }

    public void Dispose() { _bitmap?.Dispose(); _bitmap = null; }
}
