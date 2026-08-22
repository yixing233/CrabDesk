using System;
using System.Drawing;
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
        var family = GetDrawingFontFamily();
        var glyph = GetGlyph(icon);
        if (family is null || glyph.Length == 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var state = graphics.Save();
        try
        {
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var font = new Font(
                family,
                Math.Max(1, emSize),
                FontStyle.Regular,
                GraphicsUnit.Pixel);
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
            _wpfFontFamily = File.Exists(fontPath)
                ? new WpfMedia.FontFamily(new Uri(fontPath, UriKind.Absolute), "#lucide")
                : new WpfMedia.FontFamily("lucide");
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

    private static string GetFontPath() =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "lucide.ttf");
}
