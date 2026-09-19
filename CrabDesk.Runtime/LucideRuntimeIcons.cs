using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;
using WpfMedia = System.Windows.Media;

namespace CrabDesk.Runtime;

internal enum LucideRuntimeIcon
{
    AppWindow,
    ArrowDown,
    ArrowDownAz,
    ArrowRight,
    ArrowUp,
    BringToFront,
    Check,
    ChevronsUpDown,
    ChevronRight,
    CircleAlert,
    ClipboardPaste,
    Cog,
    FolderInput,
    FolderOpen,
    Layers,
    LayoutGrid,
    List,
    ListFilter,
    LogOut,
    Menu,
    Palette,
    PackagePlus,
    PanelTopOpen,
    Pause,
    Pencil,
    Play,
    Plus,
    Power,
    RefreshCw,
    Search,
    SendToBack,
    Sparkles,
    SquarePlus,
    SunMoon,
    Tags,
    ToggleLeft,
    ToggleRight,
    Trash2,
    TriangleAlert,
    X
}

internal static class LucideRuntimeIcons
{
    private static readonly object FontSync = new();
    private static PrivateFontCollection? _fontCollection;
    private static FontFamily? _drawingFontFamily;
    private static readonly Dictionary<int, Font> DrawingFonts = [];
    private static readonly Dictionary<(LucideRuntimeIcon Icon, int FontSizeHundredths), GraphicsPath> GlyphPaths = [];
    private static WpfMedia.FontFamily? _wpfFontFamily;
    private static bool _fontLoadAttempted;

    internal static string GetGlyph(LucideRuntimeIcon icon) => icon switch
    {
        LucideRuntimeIcon.AppWindow => "\uE426",
        LucideRuntimeIcon.ArrowDown => "\uE042",
        LucideRuntimeIcon.ArrowDownAz => "\uE415",
        LucideRuntimeIcon.ArrowRight => "\uE049",
        LucideRuntimeIcon.ArrowUp => "\uE04A",
        LucideRuntimeIcon.BringToFront => "\uE4EF",
        LucideRuntimeIcon.Check => "\uE06C",
        LucideRuntimeIcon.ChevronsUpDown => "\uE211",
        LucideRuntimeIcon.ChevronRight => "\uE06F",
        LucideRuntimeIcon.CircleAlert => "\uE077",
        LucideRuntimeIcon.ClipboardPaste => "\uE3E8",
        LucideRuntimeIcon.Cog => "\uE30B",
        LucideRuntimeIcon.FolderInput => "\uE334",
        LucideRuntimeIcon.FolderOpen => "\uE247",
        LucideRuntimeIcon.Layers => "\uE529",
        LucideRuntimeIcon.LayoutGrid => "\uE0FF",
        LucideRuntimeIcon.List => "\uE106",
        LucideRuntimeIcon.ListFilter => "\uE460",
        LucideRuntimeIcon.LogOut => "\uE10E",
        LucideRuntimeIcon.Menu => "\uE115",
        LucideRuntimeIcon.Palette => "\uE1DD",
        LucideRuntimeIcon.PackagePlus => "\uE268",
        LucideRuntimeIcon.PanelTopOpen => "\uE438",
        LucideRuntimeIcon.Pause => "\uE12E",
        LucideRuntimeIcon.Pencil => "\uE1F9",
        LucideRuntimeIcon.Play => "\uE13C",
        LucideRuntimeIcon.Plus => "\uE13D",
        LucideRuntimeIcon.Power => "\uE140",
        LucideRuntimeIcon.RefreshCw => "\uE145",
        LucideRuntimeIcon.Search => "\uE151",
        LucideRuntimeIcon.SendToBack => "\uE4F3",
        LucideRuntimeIcon.Sparkles => "\uE412",
        LucideRuntimeIcon.SquarePlus => "\uE173",
        LucideRuntimeIcon.SunMoon => "\uE2B2",
        LucideRuntimeIcon.Tags => "\uE35C",
        LucideRuntimeIcon.ToggleLeft => "\uE18B",
        LucideRuntimeIcon.ToggleRight => "\uE18C",
        LucideRuntimeIcon.Trash2 => "\uE18E",
        LucideRuntimeIcon.TriangleAlert => "\uE193",
        LucideRuntimeIcon.X => "\uE1B2",
        _ => string.Empty
    };

    internal static void Draw(
        Graphics graphics,
        LucideRuntimeIcon icon,
        RectangleF bounds,
        Color color,
        float emSize)
    {
        if (TryDrawVectorIcon(graphics, icon, bounds, color, emSize))
        {
            return;
        }

        var font = GetDrawingFont(AlignEmSizeToPhysicalPixels(
            emSize,
            GetGraphicsScale(graphics)));
        if (font is null || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        using var path = GetGlyphPath(icon, font);
        if (path is null)
        {
            return;
        }

        var state = graphics.Save();
        try
        {
            // Lucide is an outline font. Its thin strokes need an integer
            // physical-pixel grid on the transparent title-action overlay.
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var translation = new Matrix();
            translation.Translate(
                bounds.Left + bounds.Width / 2f,
                bounds.Top + bounds.Height / 2f,
                MatrixOrder.Append);
            path.Transform(translation);

            // Fill the glyph outline as a vector path instead of asking GDI+
            // to rasterize text directly. This keeps Lucide's thin strokes
            // smooth at the small title-bar sizes used by the box actions.
            using var brush = new SolidBrush(color);
            graphics.FillPath(brush, path);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    // Extracting a glyph outline from the font is the expensive part of an
    // icon draw, and a menu repaints the same icons on every hover change.
    // Keep one outline per icon and size, centred on the origin, and hand
    // out clones for callers to position.
    private static GraphicsPath? GetGlyphPath(LucideRuntimeIcon icon, Font font)
    {
        var glyph = GetGlyph(icon);
        if (glyph.Length == 0)
        {
            return null;
        }

        var key = (icon, (int)Math.Round(font.Size * 100f));
        lock (FontSync)
        {
            if (!GlyphPaths.TryGetValue(key, out var path))
            {
                path = new GraphicsPath();
                using var format = new StringFormat(StringFormat.GenericTypographic)
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    FormatFlags = StringFormatFlags.NoClip
                };
                path.AddString(
                    glyph,
                    font.FontFamily,
                    (int)font.Style,
                    font.Size,
                    PointF.Empty,
                    format);
                var glyphBounds = path.GetBounds();
                if (glyphBounds.IsEmpty)
                {
                    path.Dispose();
                    return null;
                }

                using var centre = new Matrix();
                centre.Translate(
                    -(glyphBounds.Left + glyphBounds.Width / 2f),
                    -(glyphBounds.Top + glyphBounds.Height / 2f),
                    MatrixOrder.Append);
                path.Transform(centre);
                GlyphPaths[key] = path;
            }
            return (GraphicsPath)path.Clone();
        }
    }

    private static bool TryDrawVectorIcon(
        Graphics graphics,
        LucideRuntimeIcon icon,
        RectangleF bounds,
        Color color,
        float emSize)
    {
        if (icon is not (
                LucideRuntimeIcon.Search or
                LucideRuntimeIcon.Menu or
                LucideRuntimeIcon.ChevronsUpDown) ||
            bounds.Width <= 0 || bounds.Height <= 0 ||
            !float.IsFinite(emSize) || emSize <= 0)
        {
            return false;
        }

        var size = AlignEmSizeToPhysicalPixels(emSize, GetGraphicsScale(graphics));
        var scale = size / 24f;
        var left = bounds.Left + (bounds.Width - size) / 2f;
        var top = bounds.Top + (bounds.Height - size) / 2f;
        var state = graphics.Save();
        try
        {
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var pen = new Pen(color, 2f * scale)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            using var path = new GraphicsPath();
            switch (icon)
            {
                case LucideRuntimeIcon.Search:
                    path.AddEllipse(
                        left + 3f * scale,
                        top + 3f * scale,
                        16f * scale,
                        16f * scale);
                    path.StartFigure();
                    path.AddLine(
                        left + 16.65f * scale,
                        top + 16.65f * scale,
                        left + 21f * scale,
                        top + 21f * scale);
                    break;
                case LucideRuntimeIcon.Menu:
                    path.AddLine(left + 4f * scale, top + 6f * scale, left + 20f * scale, top + 6f * scale);
                    path.StartFigure();
                    path.AddLine(left + 4f * scale, top + 12f * scale, left + 20f * scale, top + 12f * scale);
                    path.StartFigure();
                    path.AddLine(left + 4f * scale, top + 18f * scale, left + 20f * scale, top + 18f * scale);
                    break;
                case LucideRuntimeIcon.ChevronsUpDown:
                    path.AddLine(left + 7f * scale, top + 9f * scale, left + 12f * scale, top + 4f * scale);
                    path.AddLine(left + 12f * scale, top + 4f * scale, left + 17f * scale, top + 9f * scale);
                    path.StartFigure();
                    path.AddLine(left + 7f * scale, top + 15f * scale, left + 12f * scale, top + 20f * scale);
                    path.AddLine(left + 12f * scale, top + 20f * scale, left + 17f * scale, top + 15f * scale);
                    break;
            }

            graphics.DrawPath(pen, path);
            return true;
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    internal static void SetMenuIcon(ToolStripMenuItem item, LucideRuntimeIcon icon) =>
        item.Tag = icon;

    internal static bool TryGetMenuIcon(ToolStripItem item, out LucideRuntimeIcon icon)
    {
        if (item.Tag is LucideRuntimeIcon value)
        {
            icon = value;
            return true;
        }

        icon = default;
        return false;
    }

    internal static WpfMedia.FontFamily CreateWpfFontFamily()
    {
        lock (FontSync)
        {
            if (_wpfFontFamily is not null)
            {
                return _wpfFontFamily;
            }

            var fontPath = GetFontPath();
            if (File.Exists(fontPath))
            {
                var fontDirectory = Path.GetDirectoryName(fontPath)! + Path.DirectorySeparatorChar;
                _wpfFontFamily = new WpfMedia.FontFamily(
                    new Uri(fontDirectory, UriKind.Absolute),
                    $"./{Path.GetFileName(fontPath)}#lucide");
            }
            else
            {
                _wpfFontFamily = new WpfMedia.FontFamily("lucide");
            }
            return _wpfFontFamily;
        }
    }

    private static FontFamily? GetDrawingFontFamily()
    {
        lock (FontSync)
        {
            if (_fontLoadAttempted)
            {
                return _drawingFontFamily;
            }

            _fontLoadAttempted = true;
            try
            {
                var fontPath = GetFontPath();
                if (!File.Exists(fontPath))
                {
                    return null;
                }

                _fontCollection = new PrivateFontCollection();
                _fontCollection.AddFontFile(fontPath);
                if (_fontCollection.Families.Length > 0)
                {
                    _drawingFontFamily = _fontCollection.Families[0];
                }
            }
            catch (Exception exception)
            {
                DiagnosticLog.Error("Failed to load the Lucide runtime font.", exception);
            }

            return _drawingFontFamily;
        }
    }

    private static Font? GetDrawingFont(float emSize)
    {
        lock (FontSync)
        {
            var family = GetDrawingFontFamily();
            if (family is null)
            {
                return null;
            }

            // Preserve the fractional logical size that maps to an integer
            // physical size at the active monitor DPI.
            var fontSizeHundredths = Math.Max(100, (int)Math.Round(emSize * 100f));
            if (!DrawingFonts.TryGetValue(fontSizeHundredths, out var font))
            {
                font = new Font(
                    family,
                    fontSizeHundredths / 100f,
                    FontStyle.Regular,
                    GraphicsUnit.Pixel);
                DrawingFonts[fontSizeHundredths] = font;
            }
            return font;
        }
    }

    private static float GetGraphicsScale(Graphics graphics)
    {
        using var transform = graphics.Transform;
        var elements = transform.Elements;
        var horizontalScale = MathF.Sqrt(elements[0] * elements[0] + elements[1] * elements[1]);
        var verticalScale = MathF.Sqrt(elements[2] * elements[2] + elements[3] * elements[3]);
        var averageScale = (horizontalScale + verticalScale) / 2f;
        return averageScale > 0 && float.IsFinite(averageScale) ? averageScale : 1f;
    }

    internal static float AlignEmSizeToPhysicalPixels(float requestedEmSize, float dpiScale)
    {
        if (!float.IsFinite(requestedEmSize) || requestedEmSize <= 0 ||
            !float.IsFinite(dpiScale) || dpiScale <= 0)
        {
            return Math.Max(1f, requestedEmSize);
        }

        var physicalPixels = Math.Max(
            1,
            (int)Math.Round(requestedEmSize * dpiScale, MidpointRounding.AwayFromZero));
        return physicalPixels / dpiScale;
    }

    private static string GetFontPath() =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "lucide.ttf");
}
