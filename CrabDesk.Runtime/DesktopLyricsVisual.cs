using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using CrabDesk.Core;
using Wpf = System.Windows;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;

namespace CrabDesk.Runtime;

/// <summary>Cached lyric masks, real word timing, and transparent depth/edge treatment.</summary>
internal sealed class DesktopLyricsVisual : IDisposable
{
    private const double TransitionMilliseconds = 350;
    private const int MaximumCachedLines = 24;
    private readonly Dictionary<int, LineRaster> _lines = new();
    private WidgetLyricsDocument? _document;
    private LayoutKey? _layout;
    private Bitmap? _frame;
    private Bitmap? _staticBefore;
    private Bitmap? _staticAfter;
    private StaticFrameKey? _staticFrameKey;
    private byte[] _framePixels = Array.Empty<byte>();
    private double _fromAnchor;
    private int _targetAnchor = -1;
    private int _previousActive = -1;
    private int _active = -1;
    private DateTimeOffset _transitionStarted;
    private long _useCounter;

    private readonly record struct LayoutKey(double Width, double FontSize, double Scale, int Ink, int Accent,
        string FontFamily, int FontWeight, WidgetLyricsFontStyle FontStyle);
    private readonly record struct WordSpan(WidgetLyricWord Word, RectangleF Bounds, bool RightToLeft);
    private readonly record struct StaticFrameKey(int Anchor, int Active, int Rows, int ExpiredLines, float Width, float Height);
    private readonly record struct LinePlacement(LineRaster Raster, RectangleF Destination, float Scale, float Opacity, float Depth);

    private sealed class LineRaster : IDisposable
    {
        internal required Bitmap Sharp;
        internal required Bitmap Soft;
        internal required Bitmap Distant;
        internal required float Width;
        internal required float Height;
        internal required WordSpan[] Words;
        internal long LastUse;
        public void Dispose() { Sharp.Dispose(); Soft.Dispose(); Distant.Dispose(); }
    }

    internal bool NeedsAnimation { get; private set; }

    internal void Draw(Graphics graphics, WidgetLyricsSnapshot snapshot, RectangleF bounds,
        Color ink, Color accent, int lineCount, double fontSize, int offsetMilliseconds,
        bool wordHighlight, bool animationsEnabled, double scale, DateTimeOffset now,
        string? fontFamily = null, int fontWeight = 600, WidgetLyricsFontStyle fontStyle = WidgetLyricsFontStyle.Normal)
    {
        NeedsAnimation = false;
        if (bounds.Width < 1 || bounds.Height < 1) return;
        scale = double.IsFinite(scale) ? Math.Clamp(scale, .5, 4) : 1;
        fontSize = WidgetLyricsSettings.NormalizeFontSize(fontSize);
        lineCount = WidgetLyricsSettings.NormalizeLineCount(lineCount);
        var layout = new LayoutKey(Math.Round(bounds.Width, 2), fontSize, scale, ink.ToArgb(), accent.ToArgb(),
            WidgetLyricsSettings.NormalizeFontFamily(fontFamily), WidgetLyricsSettings.NormalizeFontWeight(fontWeight),
            WidgetLyricsSettings.NormalizeFontStyle(fontStyle));
        if (!ReferenceEquals(_document, snapshot.Document) || _layout != layout)
        {
            ClearLines();
            _document = snapshot.Document;
            _layout = layout;
            _targetAnchor = -1;
            _active = _previousActive = -1;
            _transitionStarted = default;
            _staticFrameKey = null;
        }

        EnsureFrame(bounds, scale);
        using (var canvas = CreateCanvas(_frame!, bounds))
        {
            var document = snapshot.Document;
            if (document is null || document.Lines.Count == 0)
            {
                var status = snapshot.IsLoading ? "正在查找歌词…" : document?.IsInstrumental == true ? "纯音乐"
                    : snapshot.Media is null ? "播放音乐以显示歌词" : string.IsNullOrWhiteSpace(snapshot.Error) ? "暂无歌词" : snapshot.Error;
                // Status strings share the same bounded cache as lyric lines.
                var statusKey = StringComparer.Ordinal.GetHashCode(status);
                var raster = GetLine(statusKey, new WidgetLyricLine(status, 0, 0), Math.Min(fontSize, 16), scale);
                DrawMask(canvas, raster, new RectangleF((bounds.Width - raster.Width) / 2,
                    (bounds.Height - raster.Height) / 2, raster.Width, raster.Height), ink, .65f, 0);
            }
            else
            {
                var position = (snapshot.Media?.PositionNow(now).TotalMilliseconds ?? 0)
                    + WidgetLyricsSettings.NormalizeOffset(offsetMilliseconds);
                var active = wordHighlight ? FindCoveredLine(document, position) : FindActiveLine(document, position);
                var anchor = wordHighlight && active >= 0 ? active : FindAnchor(document, position);
                var oldAnchor = CurrentAnchor(now, animationsEnabled);
                if (anchor != _targetAnchor)
                {
                    _previousActive = _active;
                    var adjacent = _targetAnchor >= 0 && Math.Abs(anchor - _targetAnchor) == 1;
                    _fromAnchor = adjacent && animationsEnabled ? oldAnchor : anchor;
                    _transitionStarted = adjacent && animationsEnabled ? now : default;
                    _targetAnchor = anchor;
                }
                _active = active;
                if (!animationsEnabled) _transitionStarted = default;
                var visualAnchor = CurrentAnchor(now, animationsEnabled);
                var transitioning = animationsEnabled && _transitionStarted != default
                    && (now - _transitionStarted).TotalMilliseconds < TransitionMilliseconds;
                if (!transitioning) _previousActive = -1;
                var before = (lineCount - 1) / 2;
                var after = lineCount - before - 1;
                var spacing = Math.Min(fontSize * 1.75, bounds.Height / (2 * Math.Max(before, after) + 1));
                var start = Math.Max(0, (int)Math.Floor(visualAnchor) - before - 1);
                var end = Math.Min(document.Lines.Count - 1, (int)Math.Ceiling(visualAnchor) + after + 1);
                if (!transitioning)
                {
                    PrepareStaticRows(document, start, end, anchor, active, lineCount, before, after, spacing,
                        bounds, fontSize, scale, position, ink);
                    CopyStaticFrame(canvas, _staticBefore!, bounds);
                }
                for (var index = start; index <= end; index++)
                {
                    if (!transitioning && index != active) continue;
                    var line = document.Lines[index];
                    var leaving = transitioning && _previousActive == index && active >= 0;
                    if (PlaceLine(index, line, visualAnchor, before, after, spacing, bounds, fontSize, scale,
                            position, index == active || leaving) is not { } placement) continue;
                    var raster = placement.Raster;
                    var destination = placement.Destination;
                    var size = placement.Scale;
                    var opacity = placement.Opacity;
                    var depth = placement.Depth;
                    var magnitude = Math.Abs(index - visualAnchor);
                    DrawMask(canvas, raster, destination, ink, opacity * (active == index ? .78f : .72f), depth);

                    if (index != active && !leaving) continue;
                    var highlightOpacity = opacity * (float)Math.Exp(-.3 * magnitude);
                    if (leaving) highlightOpacity *= (float)(1 - Math.Clamp(
                        (now - _transitionStarted).TotalMilliseconds / TransitionMilliseconds, 0, 1));
                    if (!wordHighlight || line.Words is not { Count: > 0 })
                    {
                        DrawMask(canvas, raster, destination, accent, highlightOpacity, depth);
                        continue;
                    }
                    using var coveragePath = new GraphicsPath(FillMode.Winding);
                    foreach (var word in raster.Words)
                    {
                        var coverage = WordProgress(word.Word, position);
                        if (coverage <= 0) continue;
                        var clip = new RectangleF(destination.X + word.Bounds.X * size,
                            destination.Y + word.Bounds.Y * size, word.Bounds.Width * size * (float)coverage,
                            word.Bounds.Height * size);
                        if (word.RightToLeft) clip.X += word.Bounds.Width * size - clip.Width;
                        coveragePath.AddRectangle(clip);
                    }
                    if (coveragePath.PointCount > 0)
                    {
                        var state = canvas.Save();
                        canvas.SetClip(coveragePath, CombineMode.Intersect);
                        DrawMask(canvas, raster, destination, accent, highlightOpacity, depth);
                        canvas.Restore(state);
                    }
                }
                if (!transitioning) CopyStaticFrame(canvas, _staticAfter!, bounds);
                var lastEnd = document.Lines[document.Lines.Count - 1].EndMilliseconds;
                NeedsAnimation = transitioning || snapshot.Media?.IsPlaying == true && position < lastEnd;
            }
        }
        FadeEdges(_frame!, lineCount == 1 ? .18 : .16);
        graphics.DrawImage(_frame!, bounds, new RectangleF(0, 0, _frame!.Width, _frame.Height), GraphicsUnit.Pixel);
    }

    private static Graphics CreateCanvas(Bitmap bitmap, RectangleF bounds)
    {
        var canvas = Graphics.FromImage(bitmap);
        canvas.Clear(Color.Transparent);
        canvas.ScaleTransform(bitmap.Width / bounds.Width, bitmap.Height / bounds.Height);
        canvas.CompositingQuality = CompositingQuality.HighQuality;
        canvas.InterpolationMode = InterpolationMode.HighQualityBicubic;
        canvas.PixelOffsetMode = PixelOffsetMode.HighQuality;
        return canvas;
    }

    private static void CopyStaticFrame(Graphics canvas, Bitmap bitmap, RectangleF bounds) =>
        canvas.DrawImage(bitmap, new RectangleF(0, 0, bounds.Width, bounds.Height),
            new RectangleF(0, 0, bitmap.Width, bitmap.Height), GraphicsUnit.Pixel);

    private void PrepareStaticRows(WidgetLyricsDocument document, int start, int end, int anchor, int active,
        int rows, int before, int after, double spacing, RectangleF bounds, double fontSize, double scale,
        double position, Color ink)
    {
        var expired = 0;
        for (var index = start; index <= end; index++)
            if (index != active && position >= document.Lines[index].EndMilliseconds) expired |= 1 << (index - start);
        var key = new StaticFrameKey(anchor, active, rows, expired, bounds.Width, bounds.Height);
        if (_staticFrameKey == key) return;
        _staticBefore ??= new Bitmap(_frame!.Width, _frame.Height, PixelFormat.Format32bppPArgb);
        _staticAfter ??= new Bitmap(_frame!.Width, _frame.Height, PixelFormat.Format32bppPArgb);
        using var earlier = CreateCanvas(_staticBefore, bounds);
        using var later = CreateCanvas(_staticAfter, bounds);
        for (var index = start; index <= end; index++)
        {
            if (index == active) continue;
            if (PlaceLine(index, document.Lines[index], anchor, before, after, spacing, bounds,
                    fontSize, scale, position, scrolling: false) is not { } placement) continue;
            // Preserve row paint order even when large fonts make adjacent masks overlap.
            DrawMask(index < active ? earlier : later, placement.Raster, placement.Destination,
                ink, placement.Opacity * .72f, placement.Depth);
        }
        _staticFrameKey = key;
    }

    private LinePlacement? PlaceLine(int index, WidgetLyricLine line, double anchor, int before, int after,
        double spacing, RectangleF bounds, double fontSize, double scale, double position, bool scrolling)
    {
        var distance = index - anchor;
        var magnitude = Math.Abs(distance);
        var window = Math.Clamp(distance < 0 ? before + 1 + distance : after + 1 - distance, 0, 1);
        if (window <= 0) return null;
        var size = (float)Math.Max(.55, Math.Exp(-.135 * magnitude));
        var opacity = (float)(Math.Exp(-.47 * magnitude) * window);
        var raster = GetLine(index, line, fontSize, scale);
        var viewportWidth = bounds.Width / size;
        var overflow = Math.Max(0, raster.Width - viewportWidth);
        var offset = scrolling ? HorizontalOffset(raster, line, position, viewportWidth)
            : position >= line.EndMilliseconds ? overflow : 0;
        var destination = new RectangleF(overflow > 0 ? -offset * size : (bounds.Width - raster.Width * size) / 2,
            (float)(bounds.Height / 2 + distance * spacing - raster.Height * size / 2), raster.Width * size, raster.Height * size);
        return new(raster, destination, size, opacity, (float)Math.Min(2, magnitude * .7));
    }

    /// <summary>Half-open intervals keep prelude, inter-line gaps and the ending unhighlighted.</summary>
    internal static int FindActiveLine(WidgetLyricsDocument document, double milliseconds)
    {
        var candidate = FindAnchor(document, milliseconds);
        for (var index = candidate; index >= 0; index--)
        {
            var line = document.Lines[index];
            if (line.StartMilliseconds <= milliseconds && milliseconds < line.EndMilliseconds) return index;
            // A normal ordered document only needs the latest line; the previous line can overlap a translation.
            if (index < candidate - 1) break;
        }
        return -1;
    }

    /// <summary>Keep completed word coverage until another line has visible coverage to replace it.</summary>
    private static int FindCoveredLine(WidgetLyricsDocument document, double milliseconds)
    {
        if (document.Lines.Count == 0 || milliseconds >= document.Lines[document.Lines.Count - 1].EndMilliseconds)
            return -1;
        for (var index = FindAnchor(document, milliseconds); index >= 0; index--)
        {
            var line = document.Lines[index];
            if (line.StartMilliseconds > milliseconds) continue;
            if (line.Words is { Count: > 0 })
            {
                foreach (var word in line.Words)
                    if (!string.IsNullOrWhiteSpace(word.Text) && WordProgress(word, milliseconds) > 0) return index;
            }
            else if (milliseconds < line.EndMilliseconds) return index;
        }
        return -1;
    }

    internal static double WordProgress(WidgetLyricWord word, double milliseconds)
    {
        if (milliseconds < word.StartMilliseconds) return 0;
        var duration = word.EndMilliseconds - word.StartMilliseconds;
        return duration <= 0 ? 1 : Math.Clamp((milliseconds - word.StartMilliseconds) / duration, 0, 1);
    }

    private static float HorizontalOffset(LineRaster raster, WidgetLyricLine line, double position, float viewportWidth)
    {
        var overflow = Math.Max(0, raster.Width - viewportWidth);
        if (overflow == 0) return 0;
        if (raster.Words.Length == 0)
        {
            var duration = line.EndMilliseconds - line.StartMilliseconds;
            var progress = duration <= 0 ? 1 : Math.Clamp((position - line.StartMilliseconds) / duration, 0, 1);
            return overflow * (float)progress;
        }
        var focus = 0f;
        foreach (var word in raster.Words)
        {
            var coverage = WordProgress(word.Word, position);
            if (coverage <= 0) continue;
            focus = word.RightToLeft ? word.Bounds.Right - word.Bounds.Width * (float)coverage
                : word.Bounds.X + word.Bounds.Width * (float)coverage;
        }
        return Math.Clamp(focus - viewportWidth * .6f, 0, overflow);
    }

    private static int FindAnchor(WidgetLyricsDocument document, double milliseconds)
    {
        if (document.Lines.Count == 0) return -1;
        var low = 0;
        var high = document.Lines.Count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (document.Lines[middle].StartMilliseconds <= milliseconds) low = middle + 1;
            else high = middle - 1;
        }
        return Math.Max(0, high);
    }

    private double CurrentAnchor(DateTimeOffset now, bool animationsEnabled)
    {
        if (_targetAnchor < 0) return 0;
        if (!animationsEnabled || _transitionStarted == default) return _targetAnchor;
        var t = Math.Clamp((now - _transitionStarted).TotalMilliseconds / TransitionMilliseconds, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3);
        return _fromAnchor + (_targetAnchor - _fromAnchor) * eased;
    }

    private LineRaster GetLine(int index, WidgetLyricLine line, double fontSize, double scale)
    {
        if (_lines.TryGetValue(index, out var cached)) { cached.LastUse = ++_useCounter; return cached; }
        if (_lines.Count >= MaximumCachedLines)
        {
            var oldest = 0;
            var age = long.MaxValue;
            foreach (var entry in _lines)
                if (entry.Value.LastUse < age) { oldest = entry.Key; age = entry.Value.LastUse; }
            _lines[oldest].Dispose();
            _lines.Remove(oldest);
        }
        var raster = CreateLine(line, fontSize, scale, _layout!.Value);
        raster.LastUse = ++_useCounter;
        _lines.Add(index, raster);
        return raster;
    }

    private static LineRaster CreateLine(WidgetLyricLine line, double fontSize, double scale, LayoutKey layout)
    {
        const double padding = 8;
        var text = line.Text.Replace('\r', ' ').Replace('\n', ' ');
        var typeface = new Media.Typeface(new Media.FontFamily(layout.FontFamily),
            layout.FontStyle == WidgetLyricsFontStyle.Italic ? Wpf.FontStyles.Italic : Wpf.FontStyles.Normal,
            Wpf.FontWeight.FromOpenTypeWeight(layout.FontWeight), Wpf.FontStretches.Normal);
        var formatted = new Media.FormattedText(text, CultureInfo.CurrentUICulture,
            Wpf.FlowDirection.LeftToRight, typeface, fontSize, Media.Brushes.White, null, Media.TextFormattingMode.Ideal, scale);
        formatted.MaxLineCount = 1;
        var logicalWidth = formatted.WidthIncludingTrailingWhitespace + padding * 2;
        var logicalHeight = formatted.Height + padding * 2;
        var width = Math.Max(1, (int)Math.Ceiling(logicalWidth * scale));
        var height = Math.Max(1, (int)Math.Ceiling(logicalHeight * scale));
        var visual = new Media.DrawingVisual();
        Media.VisualTreeHelper.SetRootDpi(visual, new Wpf.DpiScale(scale, scale));
        using (var drawing = visual.RenderOpen()) drawing.DrawText(formatted, new Wpf.Point(padding, padding));
        var target = new Imaging.RenderTargetBitmap(width, height, 96 * scale, 96 * scale, Media.PixelFormats.Pbgra32);
        target.Render(visual);
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try { target.CopyPixels(Wpf.Int32Rect.Empty, data.Scan0, data.Stride * height, data.Stride); }
        finally { bitmap.UnlockBits(data); }
        var words = new List<WordSpan>();
        var cursor = 0;
        if (line.Words is { Count: > 0 })
        {
            foreach (var word in line.Words)
            {
                if (string.IsNullOrEmpty(word.Text)) continue;
                var wordText = word.Text.Replace('\r', ' ').Replace('\n', ' ');
                var at = text.IndexOf(wordText, cursor, StringComparison.Ordinal);
                if (at < 0) continue;
                cursor = at + wordText.Length;
                var geometry = formatted.BuildHighlightGeometry(new Wpf.Point(padding, padding), at, wordText.Length);
                if (geometry is null || geometry.Bounds.IsEmpty) continue;
                var rect = geometry.Bounds;
                words.Add(new WordSpan(word, new RectangleF((float)rect.X, (float)rect.Y,
                    (float)rect.Width, (float)rect.Height), IsRightToLeft(wordText)));
            }
        }
        return new LineRaster
        {
            Sharp = bitmap,
            Soft = BlurMask(bitmap, Math.Max(1, (int)Math.Round(scale * .65)), 2),
            Distant = BlurMask(bitmap, Math.Max(1, (int)Math.Round(scale * 1.6)), 3),
            Width = (float)(width / scale), Height = (float)(height / scale), Words = words.ToArray()
        };
    }

    private static bool IsRightToLeft(string text)
    {
        foreach (var character in text)
        {
            if (character is >= '\u0590' and <= '\u08ff') return true;
            if (char.IsLetter(character)) return false;
        }
        return false;
    }

    private static void DrawMask(Graphics graphics, LineRaster raster, RectangleF destination, Color color, float opacity, float depth)
    {
        if (opacity <= 0) return;
        if (depth < 1)
        {
            Draw(raster.Sharp, 1 - depth);
            Draw(raster.Soft, depth);
        }
        else
        {
            Draw(raster.Soft, 2 - depth);
            Draw(raster.Distant, depth - 1);
        }
        void Draw(Bitmap bitmap, float weight)
        {
            if (weight <= .001) return;
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.Clamp);
            attributes.SetColorMatrix(new ColorMatrix
            {
                Matrix00 = color.R / 255f, Matrix11 = color.G / 255f, Matrix22 = color.B / 255f,
                Matrix33 = Math.Clamp(opacity * weight * color.A / 255f, 0, 1), Matrix44 = 1
            });
            PointF[] points = [new(destination.Left, destination.Top), new(destination.Right, destination.Top), new(destination.Left, destination.Bottom)];
            graphics.DrawImage(bitmap, points, new RectangleF(0, 0, bitmap.Width, bitmap.Height), GraphicsUnit.Pixel, attributes);
        }
    }

    private static Bitmap BlurMask(Bitmap original, int radius, int passes)
    {
        var width = original.Width;
        var height = original.Height;
        var bytes = new byte[width * height * 4];
        var data = original.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try { Marshal.Copy(data.Scan0, bytes, 0, bytes.Length); }
        finally { original.UnlockBits(data); }
        var alpha = new byte[width * height];
        var horizontal = new byte[alpha.Length];
        for (var index = 0; index < alpha.Length; index++) alpha[index] = bytes[index * 4 + 3];
        var divisor = radius * 2 + 1;
        for (var pass = 0; pass < passes; pass++)
        {
            for (var y = 0; y < height; y++)
            {
                var sum = 0;
                for (var x = -radius; x < width; x++)
                {
                    if (x + radius < width) sum += alpha[y * width + x + radius];
                    if (x - radius - 1 >= 0) sum -= alpha[y * width + x - radius - 1];
                    if (x >= 0) horizontal[y * width + x] = (byte)(sum / divisor);
                }
            }
            for (var x = 0; x < width; x++)
            {
                var sum = 0;
                for (var y = -radius; y < height; y++)
                {
                    if (y + radius < height) sum += horizontal[(y + radius) * width + x];
                    if (y - radius - 1 >= 0) sum -= horizontal[(y - radius - 1) * width + x];
                    if (y >= 0) alpha[y * width + x] = (byte)(sum / divisor);
                }
            }
        }
        for (var index = 0; index < alpha.Length; index++)
            bytes[index * 4] = bytes[index * 4 + 1] = bytes[index * 4 + 2] = bytes[index * 4 + 3] = alpha[index];
        var result = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        data = result.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try { Marshal.Copy(bytes, 0, data.Scan0, bytes.Length); }
        finally { result.UnlockBits(data); }
        return result;
    }

    private void EnsureFrame(RectangleF bounds, double scale)
    {
        var width = Math.Clamp((int)Math.Ceiling(bounds.Width * scale), 1, 4096);
        var height = Math.Clamp((int)Math.Ceiling(bounds.Height * scale), 1, 4096);
        if (_frame?.Width == width && _frame.Height == height) return;
        _frame?.Dispose();
        _staticBefore?.Dispose(); _staticBefore = null;
        _staticAfter?.Dispose(); _staticAfter = null;
        _staticFrameKey = null;
        _frame = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        _framePixels = new byte[width * height * 4];
    }

    private void FadeEdges(Bitmap bitmap, double fraction)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        try
        {
            // Multiplying premultiplied RGB along with alpha keeps the fade transparent on any desktop.
            Marshal.Copy(data.Scan0, _framePixels, 0, _framePixels.Length);
            var edge = Math.Max(1, bitmap.Height * fraction);
            for (var y = 0; y < bitmap.Height; y++)
            {
                var t = Math.Clamp(Math.Min(y, bitmap.Height - 1 - y) / edge, 0, 1);
                if (t >= 1) continue;
                var factor = t * t * (3 - 2 * t);
                var start = y * data.Stride;
                var end = start + bitmap.Width * 4;
                for (var index = start; index < end; index++) _framePixels[index] = (byte)(_framePixels[index] * factor);
            }
            var side = Math.Max(1, bitmap.Width * .03);
            for (var x = 0; x < side; x++)
            {
                var t = x / side;
                var factor = t * t * (3 - 2 * t);
                for (var y = 0; y < bitmap.Height; y++)
                {
                    var left = y * data.Stride + x * 4;
                    var right = y * data.Stride + (bitmap.Width - 1 - x) * 4;
                    for (var channel = 0; channel < 4; channel++)
                    {
                        _framePixels[left + channel] = (byte)(_framePixels[left + channel] * factor);
                        _framePixels[right + channel] = (byte)(_framePixels[right + channel] * factor);
                    }
                }
            }
            Marshal.Copy(_framePixels, 0, data.Scan0, _framePixels.Length);
        }
        finally { bitmap.UnlockBits(data); }
    }

    private void ClearLines()
    {
        foreach (var line in _lines.Values) line.Dispose();
        _lines.Clear();
    }

    public void Dispose()
    {
        ClearLines();
        _frame?.Dispose();
        _frame = null;
        _staticBefore?.Dispose(); _staticBefore = null;
        _staticAfter?.Dispose(); _staticAfter = null;
        _staticFrameKey = null;
        _framePixels = Array.Empty<byte>();
        NeedsAnimation = false;
    }
}
