using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CrabDesk.Bootstrapper;

internal enum LucideIcon
{
    ShieldCheck,
    FolderOpen,
    Check,
    CheckCircle,
    CircleAlert,
    RefreshCw,
    Package,
    Download,
    Sparkles,
    HardDrive,
    Clock,
    Layers
}

internal static class LucideInstallerIcons
{
    private static readonly object SyncLock = new();
    private static PrivateFontCollection? _fontCollection;
    private static FontFamily? _fontFamily;
    private static readonly Dictionary<float, Font> FontCache = [];
    private static Image? _appLogo;
    private static IntPtr _fontBuffer = IntPtr.Zero;

    public static string GetGlyph(LucideIcon icon) => icon switch
    {
        LucideIcon.ShieldCheck => "\uE1FF",
        LucideIcon.FolderOpen => "\uE247",
        LucideIcon.Check => "\uE06C",
        LucideIcon.CheckCircle => "\uE07C",
        LucideIcon.CircleAlert => "\uE077",
        LucideIcon.RefreshCw => "\uE145",
        LucideIcon.Package => "\uE129",
        LucideIcon.Download => "\uE0B2",
        LucideIcon.Sparkles => "\uE412",
        LucideIcon.HardDrive => "\uE0ED",
        LucideIcon.Clock => "\uE087",
        LucideIcon.Layers => "\uE529",
        _ => string.Empty
    };

    public static void Initialize()
    {
        lock (SyncLock)
        {
            if (_fontFamily != null) return;

            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using var fontStream = assembly.GetManifestResourceStream("Assets.lucide.ttf");
                if (fontStream != null)
                {
                    var length = (int)fontStream.Length;
                    var bytes = new byte[length];
                    fontStream.ReadExactly(bytes, 0, length);

                    _fontBuffer = Marshal.AllocCoTaskMem(length);
                    Marshal.Copy(bytes, 0, _fontBuffer, length);

                    _fontCollection = new PrivateFontCollection();
                    _fontCollection.AddMemoryFont(_fontBuffer, length);
                    if (_fontCollection.Families.Length > 0)
                    {
                        _fontFamily = _fontCollection.Families[0];
                    }
                }
            }
            catch
            {
            }

            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using var logoStream = assembly.GetManifestResourceStream("Assets.CrabDesk-256.png");
                if (logoStream != null)
                {
                    _appLogo = Image.FromStream(logoStream);
                }
            }
            catch
            {
            }
        }
    }

    public static Font? GetFont(float size)
    {
        Initialize();
        if (_fontFamily == null) return null;

        lock (SyncLock)
        {
            if (FontCache.TryGetValue(size, out var existing))
            {
                return existing;
            }

            var font = new Font(_fontFamily, size, FontStyle.Regular, GraphicsUnit.Pixel);
            FontCache[size] = font;
            return font;
        }
    }

    public static void DrawAppLogo(Graphics g, Rectangle bounds)
    {
        Initialize();
        if (_appLogo != null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_appLogo, bounds);
        }
    }

    public static void DrawIcon(Graphics g, LucideIcon icon, RectangleF bounds, Color color, float emSize)
    {
        var glyph = GetGlyph(icon);
        if (string.IsNullOrEmpty(glyph)) return;

        var font = GetFont(emSize);
        if (font == null || _fontFamily == null) return;

        var previousSmoothingMode = g.SmoothingMode;
        var previousTransform = g.Transform;

        g.SmoothingMode = SmoothingMode.AntiAlias;

        var centerX = bounds.X + bounds.Width / 2f;
        var centerY = bounds.Y + bounds.Height / 2f;

        using (var path = new GraphicsPath())
        {
            var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip
            };

            path.AddString(glyph, _fontFamily, (int)FontStyle.Regular, emSize, new PointF(0, 0), sf);
            var glyphBounds = path.GetBounds();

            var glyphCenterX = glyphBounds.X + glyphBounds.Width / 2f;
            var glyphCenterY = glyphBounds.Y + glyphBounds.Height / 2f;

            using var matrix = previousTransform.Clone();
            matrix.Translate(centerX - glyphCenterX, centerY - glyphCenterY);
            g.Transform = matrix;

            using var brush = new SolidBrush(color);
            g.FillPath(brush, path);
        }

        g.Transform = previousTransform;
        g.SmoothingMode = previousSmoothingMode;
    }

    public static void DrawIconRotated(Graphics g, LucideIcon icon, RectangleF bounds, Color color, float emSize, float angleDegrees)
    {
        var glyph = GetGlyph(icon);
        if (string.IsNullOrEmpty(glyph)) return;

        var font = GetFont(emSize);
        if (font == null || _fontFamily == null) return;

        var previousSmoothingMode = g.SmoothingMode;
        var previousTransform = g.Transform;

        g.SmoothingMode = SmoothingMode.AntiAlias;

        var centerX = bounds.X + bounds.Width / 2f;
        var centerY = bounds.Y + bounds.Height / 2f;

        using (var path = new GraphicsPath())
        {
            var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip
            };

            path.AddString(glyph, _fontFamily, (int)FontStyle.Regular, emSize, new PointF(0, 0), sf);
            var glyphBounds = path.GetBounds();

            // Calculate precise centroid of glyph geometry to perfectly center rotation
            var glyphCenterX = glyphBounds.X + glyphBounds.Width / 2f;
            var glyphCenterY = glyphBounds.Y + glyphBounds.Height / 2f;

            using var matrix = previousTransform.Clone();
            matrix.Translate(centerX, centerY);
            matrix.Rotate(angleDegrees);
            matrix.Translate(-glyphCenterX, -glyphCenterY);

            g.Transform = matrix;

            using var brush = new SolidBrush(color);
            g.FillPath(brush, path);
        }

        g.Transform = previousTransform;
        g.SmoothingMode = previousSmoothingMode;
    }
}
