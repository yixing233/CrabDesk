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

/// <summary>Cached lyric rendering with real word timing and a gradient blur at the viewport edges.</summary>
internal sealed class DesktopLyricsVisual : IDisposable
{
    private const double TransitionMilliseconds = 350;
    private const int MaximumCachedLines = 24;
    private int _maximumCachedLines = MaximumCachedLines;
    private readonly Dictionary<int, LineRaster> _lines = new();
    private WidgetLyricsDocument? _document;
    private LayoutKey? _layout;
    private Bitmap? _frame;
    private Bitmap? _staticBefore;
    private Bitmap? _staticAfter;
    private StaticFrameKey? _staticFrameKey;
    private readonly DesktopLyricsEdgeBlur _edgeBlur = new();
    private Bitmap? _scrollRow;
    private byte[] _scrollRowPixels = Array.Empty<byte>();
    private int[] _scrollSampleX = Array.Empty<int>();
    private double _fromAnchor;
    private int _targetAnchor = -1;
    private int _previousActive = -1;
    private int _active = -1;
    private DateTimeOffset _transitionStarted;
    private long _useCounter;

    private readonly record struct LayoutKey(double Width, double FontSize, double Scale, int Ink, int Accent,
        string FontFamily, int FontWeight, WidgetLyricsFontStyle FontStyle);
    private readonly record struct WordSpan(WidgetLyricWord Word, RectangleF Bounds, bool RightToLeft);
    private readonly record struct StaticFrameKey(int Anchor, int Active, int Rows, int ExpiredLines, float Width, float Height, double Spacing);
    private readonly record struct LinePlacement(LineRaster Raster, RectangleF Destination);

    private sealed class LineRaster : IDisposable
    {
        internal required Bitmap Sharp;
        internal required byte[] SharpAlpha;
        internal required float Width;
        internal required float Height;
        internal required WordSpan[] Words;
        internal long LastUse;
        public void Dispose() => Sharp.Dispose();
    }

    internal bool NeedsAnimation { get; private set; }

    internal void Draw(Graphics graphics, WidgetLyricsSnapshot snapshot, RectangleF bounds,
        Color ink, Color accent, double lineSpacing, double fontSize, int offsetMilliseconds,
        bool wordHighlight, bool animationsEnabled, double scale, DateTimeOffset now,
        string? fontFamily = null, int fontWeight = 600, WidgetLyricsFontStyle fontStyle = WidgetLyricsFontStyle.Normal)
    {
        NeedsAnimation = false;
        if (bounds.Width < 1 || bounds.Height < 1) return;
        scale = double.IsFinite(scale) ? Math.Clamp(scale, .5, 4) : 1;
        fontSize = WidgetLyricsSettings.NormalizeFontSize(fontSize);
        var spacing = fontSize * WidgetLyricsSettings.NormalizeLineSpacing(lineSpacing);
        var radius = Math.Max(0, (int)Math.Floor((bounds.Height - fontSize) / (2 * spacing)));
        var lineCount = radius * 2 + 1;
        _maximumCachedLines = Math.Max(MaximumCachedLines, lineCount + 4);
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
                DrawScrollingRow(canvas, raster, new RectangleF((bounds.Width - raster.Width) / 2,
                    (bounds.Height - raster.Height) / 2, raster.Width, raster.Height), ink, .65f, bounds);
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
                var start = Math.Max(0, (int)Math.Floor(visualAnchor) - before - 1);
                var end = Math.Min(document.Lines.Count - 1, (int)Math.Ceiling(visualAnchor) + after + 1);
                if (!transitioning)
                {
                    PrepareStaticRows(document, start, end, anchor, active, lineCount, spacing,
                        bounds, fontSize, scale, position, ink);
                    CopyStaticFrame(canvas, _staticBefore!, bounds);
                }
                for (var index = start; index <= end; index++)
                {
                    if (!transitioning && index != active) continue;
                    var line = document.Lines[index];
                    var leaving = transitioning && _previousActive == index && active >= 0;
                    if (PlaceLine(index, line, visualAnchor, spacing, bounds, fontSize, scale,
                            position, index == active || leaving) is not { } placement) continue;
                    var raster = placement.Raster;
                    var destination = placement.Destination;
                    DrawScrollingRow(canvas, raster, destination, ink, .78f, bounds);

                    if (index != active && !leaving) continue;
                    var highlightOpacity = 1f;
                    if (leaving) highlightOpacity *= (float)(1 - Math.Clamp(
                        (now - _transitionStarted).TotalMilliseconds / TransitionMilliseconds, 0, 1));
                    if (!wordHighlight || line.Words is not { Count: > 0 })
                    {
                        DrawScrollingRow(canvas, raster, destination, accent, highlightOpacity, bounds);
                        continue;
                    }
                    using var coveragePath = new GraphicsPath(FillMode.Winding);
                    foreach (var word in raster.Words)
                    {
                        var coverage = WordProgress(word.Word, position);
                        if (coverage <= 0) continue;
                        var clip = new RectangleF(destination.X + word.Bounds.X,
                            destination.Y + word.Bounds.Y, word.Bounds.Width * (float)coverage,
                            word.Bounds.Height);
                        if (word.RightToLeft) clip.X += word.Bounds.Width - clip.Width;
                        coveragePath.AddRectangle(clip);
                    }
                    if (coveragePath.PointCount > 0)
                    {
                        var state = canvas.Save();
                        canvas.SetClip(coveragePath, CombineMode.Intersect);
                        DrawScrollingRow(canvas, raster, destination, accent, highlightOpacity, bounds);
                        canvas.Restore(state);
                    }
                }
                if (!transitioning) CopyStaticFrame(canvas, _staticAfter!, bounds);
                var lastEnd = document.Lines[document.Lines.Count - 1].EndMilliseconds;
                NeedsAnimation = transitioning || snapshot.Media?.IsPlaying == true && position < lastEnd;
            }
        }
        _edgeBlur.Apply(_frame!, scale);
        var outputState = graphics.Save();
        try
        {
            // The frame already contains device-sized, antialiased glyphs.
            // Fractional DIP bounds otherwise trigger another full-frame
            // interpolation pass on every animation tick.
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.DrawImage(_frame!, bounds, new RectangleF(0, 0, _frame!.Width, _frame.Height), GraphicsUnit.Pixel);
        }
        finally { graphics.Restore(outputState); }
    }

    private static Graphics CreateCanvas(Bitmap bitmap, RectangleF bounds)
    {
        var canvas = Graphics.FromImage(bitmap);
        canvas.Clear(Color.Transparent);
        canvas.ScaleTransform(bitmap.Width / bounds.Width, bitmap.Height / bounds.Height);
        // The glyph layers already use premultiplied alpha. Gamma correction
        // would convert every pixel again for each cached layer and row.
        canvas.CompositingQuality = CompositingQuality.AssumeLinear;
        // Rows are sampled at device pixel centers by DrawScrollingRow.
        // Cached frames and rows only need compositing here.
        canvas.InterpolationMode = InterpolationMode.NearestNeighbor;
        canvas.PixelOffsetMode = PixelOffsetMode.HighQuality;
        return canvas;
    }

    private static void CopyStaticFrame(Graphics canvas, Bitmap bitmap, RectangleF bounds) =>
        canvas.DrawImage(bitmap, new RectangleF(0, 0, bounds.Width, bounds.Height),
            new RectangleF(0, 0, bitmap.Width, bitmap.Height), GraphicsUnit.Pixel);

    private void PrepareStaticRows(WidgetLyricsDocument document, int start, int end, int anchor, int active,
        int rows, double spacing, RectangleF bounds, double fontSize, double scale,
        double position, Color ink)
    {
        var expired = 0;
        for (var index = start; index <= end; index++)
            if (index != active && position >= document.Lines[index].EndMilliseconds) expired++;
        var key = new StaticFrameKey(anchor, active, rows, expired, bounds.Width, bounds.Height, spacing);
        if (_staticFrameKey == key) return;
        _staticBefore ??= new Bitmap(_frame!.Width, _frame.Height, PixelFormat.Format32bppPArgb);
        _staticAfter ??= new Bitmap(_frame!.Width, _frame.Height, PixelFormat.Format32bppPArgb);
        using var earlier = CreateCanvas(_staticBefore, bounds);
        using var later = CreateCanvas(_staticAfter, bounds);
        for (var index = start; index <= end; index++)
        {
            if (index == active) continue;
            if (PlaceLine(index, document.Lines[index], anchor, spacing, bounds,
                    fontSize, scale, position, scrolling: false) is not { } placement) continue;
            // Preserve row paint order even when large fonts make adjacent masks overlap.
            DrawScrollingRow(index < active ? earlier : later, placement.Raster, placement.Destination,
                ink, .78f, bounds);
        }
        _staticFrameKey = key;
    }

    private LinePlacement? PlaceLine(int index, WidgetLyricLine line, double anchor,
        double spacing, RectangleF bounds, double fontSize, double scale, double position, bool scrolling)
    {
        var distance = index - anchor;
        var raster = GetLine(index, line, fontSize, scale);
        var viewportWidth = bounds.Width;
        var overflow = Math.Max(0, raster.Width - viewportWidth);
        var offset = scrolling ? HorizontalOffset(raster, line, position, viewportWidth)
            : position >= line.EndMilliseconds ? overflow : 0;
        var destination = new RectangleF(overflow > 0 ? -offset : (bounds.Width - raster.Width) / 2,
            (float)(bounds.Height / 2 + distance * spacing - raster.Height / 2), raster.Width, raster.Height);
        if (destination.Bottom < -4 * scale || destination.Top > bounds.Height + 4 * scale) return null;
        return new(raster, destination);
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
        while (_lines.Count >= _maximumCachedLines)
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
            SharpAlpha = ReadAlpha(bitmap),
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

    private static byte[] ReadAlpha(Bitmap bitmap)
    {
        var alpha = new byte[bitmap.Width * bitmap.Height];
        var pixels = new byte[bitmap.Width * bitmap.Height * 4];
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try { Marshal.Copy(data.Scan0, pixels, 0, pixels.Length); }
        finally { bitmap.UnlockBits(data); }
        for (var index = 0; index < alpha.Length; index++) alpha[index] = pixels[index * 4 + 3];
        return alpha;
    }

    private void DrawScrollingRow(Graphics graphics, LineRaster raster, RectangleF destination,
        Color color, float opacity, RectangleF bounds)
    {
        if (opacity <= 0) return;
        var scaleX = _frame!.Width / bounds.Width;
        var scaleY = _frame.Height / bounds.Height;
        var left = Math.Max(0, (int)Math.Floor(destination.Left * scaleX));
        var top = Math.Max(0, (int)Math.Floor(destination.Top * scaleY));
        var right = Math.Min(_frame.Width, (int)Math.Ceiling(destination.Right * scaleX));
        var bottom = Math.Min(_frame.Height, (int)Math.Ceiling(destination.Bottom * scaleY));
        var width = right - left;
        var height = bottom - top;
        if (width <= 0 || height <= 0) return;
        var rowHeight = Math.Max(height, raster.Sharp.Height + 2);
        if (_scrollRow is null || _scrollRow.Width != _frame.Width || _scrollRow.Height < rowHeight)
        {
            _scrollRow?.Dispose();
            _scrollRow = new Bitmap(_frame.Width, rowHeight, PixelFormat.Format32bppPArgb);
            _scrollRowPixels = new byte[_scrollRow.Width * _scrollRow.Height * 4];
            _scrollSampleX = new int[_scrollRow.Width];
        }
        // Sample at device pixel centers, then copy an already sized and
        // colored row. GDI+ only composites it; it does not transform the
        // entire off-screen source text.
        var sourceWidth = raster.Sharp.Width;
        var sourceHeight = raster.Sharp.Height;
        var stepX = sourceWidth / (destination.Width * scaleX);
        var stepY = sourceHeight / (destination.Height * scaleY);
        for (var x = 0; x < width; x++)
            _scrollSampleX[x] = (int)Math.Floor(((left + x + .5f - destination.X * scaleX) * stepX - .5f) * 256);
        var inkOpacity = opacity * color.A / 255f;
        var stride = _scrollRow.Width * 4;
        for (var y = 0; y < height; y++)
        {
            var sourceY = (int)Math.Floor(((top + y + .5f - destination.Y * scaleY) * stepY - .5f) * 256);
            var row = Math.Clamp(sourceY >> 8, 0, sourceHeight - 1) * sourceWidth;
            var nextRow = Math.Clamp((sourceY >> 8) + 1, 0, sourceHeight - 1) * sourceWidth;
            var fractionY = sourceY & 255;
            for (var x = 0; x < width; x++)
            {
                var sourceX = _scrollSampleX[x];
                var column = Math.Clamp(sourceX >> 8, 0, sourceWidth - 1);
                var nextColumn = Math.Clamp((sourceX >> 8) + 1, 0, sourceWidth - 1);
                var fractionX = sourceX & 255;
                var alpha = (int)Math.Clamp(Math.Round(SampleAlpha(raster.SharpAlpha,
                    row, nextRow, column, nextColumn, fractionX, fractionY) * inkOpacity), 0, 255);
                var pixel = y * stride + x * 4;
                _scrollRowPixels[pixel] = (byte)((color.B * alpha + 127) / 255);
                _scrollRowPixels[pixel + 1] = (byte)((color.G * alpha + 127) / 255);
                _scrollRowPixels[pixel + 2] = (byte)((color.R * alpha + 127) / 255);
                _scrollRowPixels[pixel + 3] = (byte)alpha;
            }
        }
        var data = _scrollRow.LockBits(new Rectangle(0, 0, _scrollRow.Width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try { Marshal.Copy(_scrollRowPixels, 0, data.Scan0, stride * height); }
        finally { _scrollRow.UnlockBits(data); }
        graphics.DrawImage(_scrollRow, new RectangleF(left / scaleX, top / scaleY, width / scaleX, height / scaleY),
            new RectangleF(0, 0, width, height), GraphicsUnit.Pixel);
    }

    private static float SampleAlpha(byte[] alpha, int row, int nextRow, int column, int nextColumn, int fractionX, int fractionY)
    {
        var earlier = alpha[row + column] * (256 - fractionX) + alpha[row + nextColumn] * fractionX;
        var later = alpha[nextRow + column] * (256 - fractionX) + alpha[nextRow + nextColumn] * fractionX;
        return (earlier * (256 - fractionY) + later * fractionY) / 65536f;
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
        _scrollRow?.Dispose(); _scrollRow = null;
        _scrollRowPixels = Array.Empty<byte>();
        _scrollSampleX = Array.Empty<int>();
        _edgeBlur.Dispose();
        _staticBefore?.Dispose(); _staticBefore = null;
        _staticAfter?.Dispose(); _staticAfter = null;
        _staticFrameKey = null;
        NeedsAnimation = false;
    }
}
