using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;
using WpfMedia = System.Windows.Media;

namespace CrabDesk.Runtime;

/// <summary>
/// Icon names used by the desktop runtime. Each member maps to a Tabler outline
/// icon; the font codepoint is resolved from <see cref="TablerRuntimeIconGlyphs"/>.
/// </summary>
internal enum TablerRuntimeIcon
{
    AppWindow,
    ArrowDown,
    ArrowDownAz,
    ArrowRight,
    ArrowUp,
    ArrowDownToLine,
    ArrowUpToLine,
    BringToFront,
    Check,
    ChevronsUpDown,
    ChevronDown,
    ChevronLeft,
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
    RefreshCcw,
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
    X,
    Sun,
    CloudSun,
    Cloud,
    CloudRain,
    CloudSnow,
    CloudLightning,
    CloudFog,
    Haze,
    CloudHail,
    CloudOff,
    Droplets,
    Wind,
    Thermometer,
    Gauge,
    MapPin,
    Copy,
    Eraser,
    // Control centre.
    Wifi,
    WifiOff,
    Bluetooth,
    Volume2,
    Moon,
    SkipBack,
    SkipForward,
    MonitorSpeaker,
    Monitor,
    Headphones,
    VolumeX,
    SlidersHorizontal,
    Mic,
    MicOff,
    Music,
    Cast,
    ScanLine,
    Smartphone,
    Keyboard,
    Mouse,
    Network,
    Clock,
    Square,
    SquareCheck,
    CalendarDays,
    ShieldCheck,
    CheckCircle,
    Download,
    Package,
    HardDrive
}

/// <summary>
/// Maps runtime icon names to Tabler font codepoints. Kept in one table so the
/// GDI, WPF and menu-icon paths always agree on a glyph.
/// </summary>
internal static class TablerRuntimeIconGlyphs
{
    internal static readonly Dictionary<TablerRuntimeIcon, int> Codepoints = new()
    {
        [TablerRuntimeIcon.AppWindow] = 0xEFE6,
        [TablerRuntimeIcon.ArrowDown] = 0xEA16,
        [TablerRuntimeIcon.ArrowDownAz] = 0xEB5A,
        [TablerRuntimeIcon.ArrowRight] = 0xEA1F,
        [TablerRuntimeIcon.ArrowUp] = 0xEA25,
        [TablerRuntimeIcon.ArrowDownToLine] = 0xEC88,
        [TablerRuntimeIcon.ArrowUpToLine] = 0xEC8B,
        [TablerRuntimeIcon.BringToFront] = 0xFD28,
        [TablerRuntimeIcon.Check] = 0xEA5E,
        [TablerRuntimeIcon.ChevronsUpDown] = 0xEB1D,
        [TablerRuntimeIcon.ChevronDown] = 0xEA5F,
        [TablerRuntimeIcon.ChevronLeft] = 0xEA60,
        [TablerRuntimeIcon.ChevronRight] = 0xEA61,
        [TablerRuntimeIcon.CircleAlert] = 0xEA05,
        [TablerRuntimeIcon.ClipboardPaste] = 0xF299,
        [TablerRuntimeIcon.Cog] = 0xEB20,
        [TablerRuntimeIcon.FolderInput] = 0xF91B,
        [TablerRuntimeIcon.FolderOpen] = 0xFAF7,
        [TablerRuntimeIcon.Layers] = 0xEB2D,
        [TablerRuntimeIcon.LayoutGrid] = 0xEDBA,
        [TablerRuntimeIcon.List] = 0xEB6B,
        [TablerRuntimeIcon.ListFilter] = 0xEAA5,
        [TablerRuntimeIcon.LogOut] = 0xEBA8,
        [TablerRuntimeIcon.Menu] = 0xEC42,
        [TablerRuntimeIcon.Palette] = 0xEB01,
        [TablerRuntimeIcon.PackagePlus] = 0xEAFF,
        [TablerRuntimeIcon.PanelTopOpen] = 0xF28C,
        [TablerRuntimeIcon.Pause] = 0xED45,
        [TablerRuntimeIcon.Pencil] = 0xEB04,
        [TablerRuntimeIcon.Play] = 0xED46,
        [TablerRuntimeIcon.Plus] = 0xEB0B,
        [TablerRuntimeIcon.Power] = 0xEB0D,
        [TablerRuntimeIcon.RefreshCw] = 0xEB13,
        [TablerRuntimeIcon.RefreshCcw] = 0xF3AE,
        [TablerRuntimeIcon.Search] = 0xEB1C,
        [TablerRuntimeIcon.SendToBack] = 0xFD26,
        [TablerRuntimeIcon.Sparkles] = 0xF6D7,
        [TablerRuntimeIcon.SquarePlus] = 0xEB2A,
        [TablerRuntimeIcon.SunMoon] = 0xF4A3,
        [TablerRuntimeIcon.Tags] = 0xEF86,
        [TablerRuntimeIcon.ToggleLeft] = 0xEB3E,
        [TablerRuntimeIcon.ToggleRight] = 0xEB3F,
        [TablerRuntimeIcon.Trash2] = 0xEB41,
        [TablerRuntimeIcon.TriangleAlert] = 0xEA06,
        [TablerRuntimeIcon.X] = 0xEB55,
        [TablerRuntimeIcon.Sun] = 0xEB30,
        [TablerRuntimeIcon.CloudSun] = 0xF236, // sun-high stands in for the partly-cloudy state
        [TablerRuntimeIcon.Cloud] = 0xEA76,
        [TablerRuntimeIcon.CloudRain] = 0xEA72,
        [TablerRuntimeIcon.CloudSnow] = 0xEA73,
        [TablerRuntimeIcon.CloudLightning] = 0xEA74,
        [TablerRuntimeIcon.CloudFog] = 0xECD9,
        [TablerRuntimeIcon.Haze] = 0xEFAA,
        [TablerRuntimeIcon.CloudHail] = 0xEA74,
        [TablerRuntimeIcon.CloudOff] = 0xED3E,
        [TablerRuntimeIcon.Droplets] = 0xFC12,
        [TablerRuntimeIcon.Wind] = 0xEC34,
        [TablerRuntimeIcon.Thermometer] = 0xEF67,
        [TablerRuntimeIcon.Gauge] = 0xEAB1,
        [TablerRuntimeIcon.MapPin] = 0xEAE8,
        [TablerRuntimeIcon.Copy] = 0xEA7A,
        [TablerRuntimeIcon.Eraser] = 0xEB8B,
        [TablerRuntimeIcon.Wifi] = 0xEB52,
        [TablerRuntimeIcon.WifiOff] = 0xECFA,
        [TablerRuntimeIcon.Bluetooth] = 0xEA37,
        [TablerRuntimeIcon.Volume2] = 0xEB51,
        [TablerRuntimeIcon.Moon] = 0xEAF8,
        [TablerRuntimeIcon.SkipBack] = 0xED48,
        [TablerRuntimeIcon.SkipForward] = 0xED49,
        [TablerRuntimeIcon.MonitorSpeaker] = 0xEA89,
        [TablerRuntimeIcon.Monitor] = 0xEA89,
        [TablerRuntimeIcon.Headphones] = 0xEABD,
        [TablerRuntimeIcon.VolumeX] = 0xF1C3,
        [TablerRuntimeIcon.SlidersHorizontal] = 0xEC38,
        [TablerRuntimeIcon.Mic] = 0xEAF0,
        [TablerRuntimeIcon.MicOff] = 0xED16,
        [TablerRuntimeIcon.Music] = 0xEAFC,
        [TablerRuntimeIcon.Cast] = 0xEA56,
        [TablerRuntimeIcon.ScanLine] = 0xEBC8,
        [TablerRuntimeIcon.Smartphone] = 0xEA8A,
        [TablerRuntimeIcon.Keyboard] = 0xEBD6,
        [TablerRuntimeIcon.Mouse] = 0xEAF9,
        [TablerRuntimeIcon.Network] = 0xF09F,
        [TablerRuntimeIcon.Clock] = 0xEA70,
        [TablerRuntimeIcon.Square] = 0xEB2C,
        [TablerRuntimeIcon.SquareCheck] = 0xEB28,
        [TablerRuntimeIcon.CalendarDays] = 0xEA52,
        [TablerRuntimeIcon.ShieldCheck] = 0xEB22,
        [TablerRuntimeIcon.CheckCircle] = 0xEA67,
        [TablerRuntimeIcon.Download] = 0xEA96,
        [TablerRuntimeIcon.Package] = 0xEAFF,
        [TablerRuntimeIcon.HardDrive] = 0xEB62
    };
}

internal static class TablerRuntimeIcons
{
    private static readonly object FontSync = new();
    private static PrivateFontCollection? _fontCollection;
    private static FontFamily? _drawingFontFamily;
    private static readonly Dictionary<int, Font> DrawingFonts = [];
    private static readonly Dictionary<(TablerRuntimeIcon Icon, int FontSizeHundredths), GraphicsPath> GlyphPaths = [];
    private static WpfMedia.FontFamily? _wpfFontFamily;
    private static bool _fontLoadAttempted;

    internal static string GetGlyph(TablerRuntimeIcon icon) =>
        TablerRuntimeIconGlyphs.Codepoints.TryGetValue(icon, out var codePoint)
            ? char.ConvertFromUtf32(codePoint)
            : string.Empty;

    internal static void Draw(
        Graphics graphics,
        TablerRuntimeIcon icon,
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
            // Tabler is an outline font. Its thin strokes need an integer
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
            // to rasterize text directly. This keeps Tabler's thin strokes
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
    private static GraphicsPath? GetGlyphPath(TablerRuntimeIcon icon, Font font)
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
        TablerRuntimeIcon icon,
        RectangleF bounds,
        Color color,
        float emSize)
    {
        if (icon is not (
                TablerRuntimeIcon.Search or
                TablerRuntimeIcon.Menu or
                TablerRuntimeIcon.ChevronsUpDown) ||
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
                // ti-search: circle at (10,10) r=7 plus the handle to (21,21).
                case TablerRuntimeIcon.Search:
                    path.AddEllipse(
                        left + 3f * scale,
                        top + 3f * scale,
                        14f * scale,
                        14f * scale);
                    path.StartFigure();
                    path.AddLine(
                        left + 15f * scale,
                        top + 15f * scale,
                        left + 21f * scale,
                        top + 21f * scale);
                    break;
                // ti-menu-2: three rules at y = 6, 12 and 18.
                case TablerRuntimeIcon.Menu:
                    path.AddLine(left + 4f * scale, top + 6f * scale, left + 20f * scale, top + 6f * scale);
                    path.StartFigure();
                    path.AddLine(left + 4f * scale, top + 12f * scale, left + 20f * scale, top + 12f * scale);
                    path.StartFigure();
                    path.AddLine(left + 4f * scale, top + 18f * scale, left + 20f * scale, top + 18f * scale);
                    break;
                // ti-selector: stacked up and down chevrons with a gap at y = 12.
                case TablerRuntimeIcon.ChevronsUpDown:
                    path.AddLine(left + 8f * scale, top + 9f * scale, left + 12f * scale, top + 5f * scale);
                    path.AddLine(left + 12f * scale, top + 5f * scale, left + 16f * scale, top + 9f * scale);
                    path.StartFigure();
                    path.AddLine(left + 8f * scale, top + 15f * scale, left + 12f * scale, top + 19f * scale);
                    path.AddLine(left + 12f * scale, top + 19f * scale, left + 16f * scale, top + 15f * scale);
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

    internal static void SetMenuIcon(ToolStripMenuItem item, TablerRuntimeIcon icon) =>
        item.Tag = icon;

    internal static bool TryGetMenuIcon(ToolStripItem item, out TablerRuntimeIcon icon)
    {
        if (item.Tag is TablerRuntimeIcon value)
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
                    $"./{Path.GetFileName(fontPath)}#tabler-icons");
            }
            else
            {
                _wpfFontFamily = new WpfMedia.FontFamily("tabler-icons");
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
                DiagnosticLog.Error("Failed to load the Tabler runtime font.", exception);
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
        Path.Combine(AppContext.BaseDirectory, "Assets", "tabler-icons.ttf");
}
