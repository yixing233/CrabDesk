using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using Wpf = System.Windows;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;

namespace CrabDesk.Runtime;

/// <summary>Composited header and slim scroll chrome in device-independent coordinates.</summary>
internal sealed class WidgetCenterChrome : IDisposable
{
    internal const float HeaderHeight = 64;
    private const float ButtonHeight = 36;
    private readonly TextRaster _title = new();
    private readonly TextRaster _add = new();
    private readonly TextRaster _back = new();
    private readonly record struct TextKey(string Text, double Size, bool Semibold, bool Centered);
    private readonly Dictionary<TextKey, TextRaster> _labels = new();
    private readonly Queue<TextKey> _labelOrder = new();

    internal static RectangleF AddBounds(float width)
    {
        width = Length(width);
        var inset = Math.Min(16, width / 10);
        var size = Math.Min(116, width - inset * 2);
        return size > 0 ? new RectangleF(width - inset - size, 14, size, ButtonHeight) : RectangleF.Empty;
    }

    internal static RectangleF TitleBounds(float width)
    {
        var add = AddBounds(width);
        var right = add.Left;
        var size = Math.Max(0, right - 12 - 24);
        return size > 0 ? new RectangleF(24, 12, size, 40) : RectangleF.Empty;
    }

    internal static RectangleF ScrollTrack(float width, float height)
    {
        width = Length(width); height = Length(height);
        var trackWidth = Math.Min(14, width);
        var rightInset = Math.Min(8, Math.Max(0, width - trackWidth));
        var top = Math.Min(HeaderHeight + 8, height);
        var trackHeight = Math.Max(0, height - top - 16);
        return trackWidth > 0 && trackHeight > 0
            ? new RectangleF(width - rightInset - trackWidth, top, trackWidth, trackHeight) : RectangleF.Empty;
    }

    internal static double MaximumScrollOffset(double viewport, double content) =>
        double.IsFinite(viewport) && double.IsFinite(content) && viewport > 0 && content > viewport
            ? content - viewport : 0;

    internal static double ClampScrollOffset(double viewport, double content, double offset) =>
        double.IsNaN(offset) ? 0 : Math.Clamp(offset, 0, MaximumScrollOffset(viewport, content));

    internal static RectangleF ScrollThumb(RectangleF track, double viewport, double content, double offset, bool expanded = false)
    {
        var maximum = MaximumScrollOffset(viewport, content);
        if (maximum == 0 || !float.IsFinite(track.X) || !float.IsFinite(track.Y) ||
            !float.IsFinite(track.Right) || !float.IsFinite(track.Bottom) ||
            !float.IsFinite(track.Width) || !float.IsFinite(track.Height) || track.Width <= 0 || track.Height <= 0)
            return RectangleF.Empty;
        var height = Math.Min(track.Height, Math.Max(28, (float)(track.Height * (viewport / content))));
        var width = Math.Min(track.Width, expanded ? 6 : 4);
        var progress = ClampScrollOffset(viewport, content, offset) / maximum;
        var top = Math.Clamp((float)(track.Top + (track.Height - height) * progress), track.Top, track.Bottom - height);
        return new RectangleF(track.X + (track.Width - width) / 2, top, width, height);
    }

    internal void DrawHeader(Graphics graphics, float width, Color foreground, bool pickerVisible,
        bool addHovered, bool addPressed, bool addFocused)
    {
        width = Length(width);
        if (width == 0) return;
        var state = graphics.Save();
        try
        {
            Prepare(graphics);
            graphics.SetClip(new RectangleF(0, 0, width, HeaderHeight), CombineMode.Intersect);
            _title.Draw(graphics, "组件中心", TitleBounds(width), foreground, 20, Wpf.FontWeights.SemiBold);
            var add = AddBounds(width);
            DrawButton(graphics, add, foreground, addHovered, addPressed, addFocused);
            if (add.Width >= 34)
            {
                using var pen = IconPen(foreground);
                var center = add.Top + add.Height / 2;
                if (pickerVisible)
                    graphics.DrawLines(pen, [new PointF(add.Left + 20, center - 5),
                        new PointF(add.Left + 15, center), new PointF(add.Left + 20, center + 5)]);
                else
                {
                    graphics.DrawLine(pen, add.Left + 13, center, add.Left + 23, center);
                    graphics.DrawLine(pen, add.Left + 18, center - 5, add.Left + 18, center + 5);
                }
                var label = new RectangleF(add.Left + 34, add.Top, Math.Max(0, add.Width - 44), add.Height);
                (pickerVisible ? _back : _add).Draw(graphics, pickerVisible ? "返回组件" : "添加组件",
                    label, foreground, 14, Wpf.FontWeights.Normal);
            }
        }
        finally { graphics.Restore(state); }
    }

    internal void DrawScroll(Graphics graphics, RectangleF track, double viewport, double content, double offset,
        Color foreground, bool hovered, bool dragging)
    {
        var thumb = ScrollThumb(track, viewport, content, offset, hovered || dragging);
        if (thumb.IsEmpty) return;
        var state = graphics.Save();
        try
        {
            Prepare(graphics);
            graphics.SetClip(track, CombineMode.Intersect);
            using var path = Rounded(thumb, thumb.Width / 2);
            using var brush = new SolidBrush(Ink(foreground, dragging ? 156 : hovered ? 120 : 80));
            graphics.FillPath(brush, path);
        }
        finally { graphics.Restore(state); }
    }

    internal void DrawText(Graphics graphics, string text, RectangleF bounds, Color color,
        double size = 13, bool semibold = false, bool centered = false)
    {
        if (string.IsNullOrEmpty(text) || bounds.Width <= 0 || bounds.Height <= 0 || !double.IsFinite(size) || size <= 0) return;
        var key = new TextKey(text, size, semibold, centered);
        if (!_labels.TryGetValue(key, out var label))
        {
            if (_labels.Count >= 64)
            {
                var oldest = _labelOrder.Dequeue();
                _labels[oldest].Dispose(); _labels.Remove(oldest);
            }
            label = new TextRaster(); _labels.Add(key, label); _labelOrder.Enqueue(key);
        }
        var state = graphics.Save();
        try
        {
            Prepare(graphics);
            graphics.SetClip(bounds, CombineMode.Intersect);
            label.Draw(graphics, text, bounds, color, size, semibold ? Wpf.FontWeights.SemiBold : Wpf.FontWeights.Normal, centered);
        }
        finally { graphics.Restore(state); }
    }

    private static float Length(float value) => float.IsFinite(value) && value > 0 ? value : 0;
    private static Color Ink(Color color, int alpha) => Color.FromArgb(color.A * alpha / 255, color.R, color.G, color.B);
    private static Pen IconPen(Color color) => new(color, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

    private static void Prepare(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
    }

    private static void DrawButton(Graphics graphics, RectangleF bounds, Color foreground, bool hovered, bool pressed, bool focused)
    {
        if (bounds.IsEmpty) return;
        using var path = Rounded(bounds, 8);
        using var brush = new SolidBrush(Ink(foreground, pressed ? 44 : hovered ? 28 : 12));
        graphics.FillPath(brush, path);
        if (!focused) return;
        bounds.Inflate(-1, -1);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        using var outline = Rounded(bounds, 7);
        using var pen = new Pen(Ink(foreground, 144), 1);
        graphics.DrawPath(pen, outline);
    }

    private static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        _title.Dispose(); _add.Dispose(); _back.Dispose();
        foreach (var label in _labels.Values) label.Dispose();
        _labels.Clear(); _labelOrder.Clear();
    }

    /// <summary>Fixed YaHei typography, independent of widget font overrides and ClearType.</summary>
    private sealed class TextRaster : IDisposable
    {
        private readonly record struct LayoutKey(string Text, int Width, int Height, float FieldWidth, float FieldHeight,
            double ScaleX, double ScaleY, int Color, double FontSize, Wpf.FontWeight Weight, bool Centered);
        private LayoutKey? _key;
        private Bitmap? _bitmap;

        internal void Draw(Graphics graphics, string value, RectangleF field, Color color, double fontSize, Wpf.FontWeight weight, bool centered = false)
        {
            if (field.Width <= 0 || field.Height <= 0) return;
            using var transform = graphics.Transform;
            var scaleX = (double)transform.Elements[0];
            var scaleY = (double)transform.Elements[3];
            if (!double.IsFinite(scaleX) || !double.IsFinite(scaleY) || scaleX <= 0 || scaleY <= 0) return;
            var width = Math.Max(1, (int)Math.Ceiling(field.Width * scaleX));
            var height = Math.Max(1, (int)Math.Ceiling(field.Height * scaleY));
            var key = new LayoutKey(value, width, height, field.Width, field.Height, scaleX, scaleY, color.ToArgb(), fontSize, weight, centered);
            if (_key != key)
            {
                var ink = new Media.SolidColorBrush(Media.Color.FromArgb(color.A, color.R, color.G, color.B));
                var text = new Media.FormattedText(value, CultureInfo.CurrentUICulture, Wpf.FlowDirection.LeftToRight,
                    new Media.Typeface(new Media.FontFamily("Microsoft YaHei UI"), Wpf.FontStyles.Normal, weight, Wpf.FontStretches.Normal),
                    fontSize, ink, null, Media.TextFormattingMode.Display, scaleY)
                    { MaxTextWidth = field.Width, MaxLineCount = 1, Trimming = Wpf.TextTrimming.CharacterEllipsis,
                        TextAlignment = centered ? Wpf.TextAlignment.Center : Wpf.TextAlignment.Left };
                var visual = new Media.DrawingVisual();
                Media.TextOptions.SetTextRenderingMode(visual, Media.TextRenderingMode.Grayscale);
                Media.TextOptions.SetTextFormattingMode(visual, Media.TextFormattingMode.Display);
                Media.TextOptions.SetTextHintingMode(visual, Media.TextHintingMode.Fixed);
                Media.VisualTreeHelper.SetRootDpi(visual, new Wpf.DpiScale(scaleX, scaleY));
                using (var drawing = visual.RenderOpen())
                {
                    drawing.PushClip(new Media.RectangleGeometry(new Wpf.Rect(0, 0, field.Width, field.Height)));
                    drawing.DrawText(text, new Wpf.Point(0, Math.Round((field.Height - text.Height) * scaleY / 2) / scaleY));
                    drawing.Pop();
                }
                var raster = new Imaging.RenderTargetBitmap(width, height, 96 * scaleX, 96 * scaleY, Media.PixelFormats.Pbgra32);
                raster.Render(visual);
                var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
                try
                {
                    var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                    try { raster.CopyPixels(Wpf.Int32Rect.Empty, data.Scan0, data.Stride * height, data.Stride); }
                    finally { bitmap.UnlockBits(data); }
                }
                catch { bitmap.Dispose(); throw; }
                _bitmap?.Dispose(); _bitmap = bitmap; _key = key;
            }
            // The raster already uses monitor pixels; fractional blits would filter its glyph edges again.
            var state = graphics.Save();
            try
            {
                graphics.PixelOffsetMode = PixelOffsetMode.None;
                PointF[] origin = [new(field.X, field.Y)];
                graphics.TransformPoints(CoordinateSpace.Device, CoordinateSpace.World, origin);
                graphics.ResetTransform();
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                // DrawImageUnscaled still honors bitmap DPI; explicit pixel rectangles keep cold and cached renders identical.
                graphics.DrawImage(_bitmap!, new Rectangle(Point.Round(origin[0]), _bitmap!.Size),
                    0, 0, width, height, GraphicsUnit.Pixel);
            }
            finally { graphics.Restore(state); }
        }

        public void Dispose() { _bitmap?.Dispose(); _bitmap = null; _key = null; }
    }
}
