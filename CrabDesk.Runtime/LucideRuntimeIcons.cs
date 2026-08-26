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
    TriangleAlert
}

internal static class LucideRuntimeIcons
{
    private static readonly object FontSync = new();
    private static PrivateFontCollection? _fontCollection;
    private static FontFamily? _drawingFontFamily;
    private static readonly Dictionary<int, Font> DrawingFonts = [];
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
        _ => string.Empty
    };

    internal static void Draw(
        Graphics graphics,
        LucideRuntimeIcon icon,
        RectangleF bounds,
        Color color,
        float emSize)
    {
        var font = GetDrawingFont(AlignEmSizeToPhysicalPixels(
            emSize,
            GetGraphicsScale(graphics)));
        var glyph = GetGlyph(icon);
        if (font is null || glyph.Length == 0 || bounds.Width <= 0 || bounds.Height <= 0)
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
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var brush = new SolidBrush(color);
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoClip
            };
            graphics.DrawString(glyph, font, brush, bounds, format);
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
