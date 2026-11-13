using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CrabDesk.Native;

/// <summary>
/// The item category used to select the plate and shortcut mark.
/// </summary>
public enum IconSkinKind
{
    App,
    Folder,
    File,
    Shortcut
}

/// <summary>
/// Flat icon colors shared by desktop icons and box icons.
/// </summary>
public sealed record IconSkinOptions(
    bool Enabled,
    Color AppPlate,
    Color FolderPlate,
    Color BadgeColor)
{
    public bool ShowShortcutArrow { get; init; } = true;

    public static readonly IconSkinOptions Disabled =
        new(false, Color.White, Color.FromArgb(0xDD, 0xE6, 0xF2), Color.White);

    public static IconSkinOptions FromSettings(
        bool enabled,
        string appPlate,
        string folderPlate,
        string badgeColor) =>
        new(
            enabled,
            ParseColor(appPlate, Color.White),
            ParseColor(folderPlate, Color.FromArgb(0xDD, 0xE6, 0xF2)),
            ParseColor(badgeColor, Color.White));

    /// <summary>
    /// A stable stamp over everything that changes the rendered pixels, so icon
    /// caches keyed on it cannot serve a plate from the previous skin.
    /// </summary>
    public int CacheStamp() =>
        HashCode.Combine(
            Enabled,
            AppPlate.ToArgb(),
            FolderPlate.ToArgb(),
            BadgeColor.ToArgb(),
            ShowShortcutArrow,
            FlatIconSkin.Version);

    private static Color ParseColor(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var text = value.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        try
        {
            return ColorTranslator.FromHtml(text);
        }
        catch (Exception)
        {
            return fallback;
        }
    }
}

/// <summary>
/// Flat desktop icons with continuous-corner app plates and folded documents.
/// </summary>
/// <remarks>
/// This is a pure pixel renderer — it never touches the shell, so applying a
/// skin changes nothing on disk and turning it off restores the stock icons.
/// Icons that already are a full bleed tile (a photo, a store asset with its
/// own rounded board) are clipped to the squircle instead of being given a
/// plate, which is what keeps them from looking double-framed.
/// </remarks>
public static partial class FlatIconSkin
{
    /// <summary>Bumped whenever the recipe changes, to invalidate icon caches.</summary>
    public const int Version = 3;

    // Ratios measured off the reference render.
    private const double ArtworkInsetFraction = 0.14;
    private const double CornerFactor = 0.225;
    private const double BadgeSeatFraction = 0.20;
    private const double BadgeCornerFraction = 0.05;
    // The seat keeps a legibility floor, but it has to be small enough that a
    // small desktop icon does not become half arrow.
    private const double BadgeMinSeatPixels = 8.0;
    // Below this edge the badge has no pixel budget to read as a badge at all;
    // the icon is left unmarked rather than stamped with an unreadable blob.
    private const int BadgeMinIconSize = 24;

    private const double FullBleedCoverage = 0.88;

    /// <summary>Extra samples per axis used to keep small icons crisp.</summary>
    private const int SupersampleBelow = 64;

    private static readonly object MaskGate = new();
    private static readonly Dictionary<int, byte[]> MaskCache = [];

    /// <summary>
    /// Renders <paramref name="source"/> as a flat icon. The returned bitmap is
    /// owned by the caller; the source is never modified.
    /// </summary>
    public static Bitmap Render(Bitmap source, int pixelSize, IconSkinKind kind, IconSkinOptions options, string? parsingName = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        var size = Math.Clamp(pixelSize, 16, 256);
        if (!options.Enabled)
        {
            var plain = source.Width == size && source.Height == size
                ? (Bitmap)source.Clone()
                : Downscale(source, size);
            if (kind != IconSkinKind.Shortcut || !options.ShowShortcutArrow || size < BadgeMinIconSize)
            {
                return plain;
            }
            using (plain)
            {
                var pixels = ReadRgba(plain);
                DrawBadge(pixels, size, Color.White);
                return ToBitmap(pixels, size);
            }
        }

        if (kind == IconSkinKind.File)
        {
            return RenderFile(source, size, options, FlatFileIconStyle.FromPath(parsingName));
        }

        var work = size <= SupersampleBelow ? size * 2 : size;

        var artwork = ReadRgba(source);
        var (artX, artY, artW, artH) = AlphaBounds(artwork, source.Width, source.Height);
        var fullBleed = IsFullBleed(artwork, source.Width, (artX, artY, artW, artH));

        var tile = new byte[work * work * 4];
        var mask = SquircleMask(work);
        var plate = kind is IconSkinKind.App or IconSkinKind.Shortcut
            ? options.AppPlate : options.FolderPlate;

        if (fullBleed)
        {
            // The artwork IS the tile: scale it to fill and let the mask shape it.
            DrawArtworkFit(artwork, source.Width, source.Height, (artX, artY, artW, artH), tile, work, fullBleed: true);
        }
        else
        {
            FillPlate(tile, work, plate);
            DrawArtworkFit(artwork, source.Width, source.Height, (artX, artY, artW, artH), tile, work, fullBleed: false);
        }

        // Both lanes end up shaped by the same mask: a plated icon is clipped back
        // to its own anti-aliased rim, and a full-bleed tile is carved into the
        // squircle instead of being left as a hard square.
        ClipToMask(tile, work, mask);

        if (kind == IconSkinKind.Shortcut && options.ShowShortcutArrow && size >= BadgeMinIconSize)
        {
            DrawBadge(tile, work, options.BadgeColor);
        }

        return work == size ? ToBitmap(tile, work) : DownsampleTile(tile, work);
    }

    // ---- shape -------------------------------------------------------------

    /// <summary>
    /// The continuous-corner squircle as a GDI+ path: three cubics per corner
    /// around a 0.225 of the edge radius, the same geometry the reference uses.
    /// </summary>
    private static GraphicsPath SquirclePath(float size)
    {
        var r = (float)(CornerFactor * size);

        PointF TL(float x, float y) => new(x * r, y * r);
        PointF TR(float x, float y) => new(size - x * r, y * r);
        PointF BR(float x, float y) => new(size - x * r, size - y * r);
        PointF BL(float x, float y) => new(x * r, size - y * r);

        var path = new GraphicsPath();
        path.StartFigure();
        path.AddLine(TL(1.528665f, 0f), TR(1.528665f, 0f));
        Corner(path, TR);
        path.AddLine(TR(0f, 1.528665f), BR(0f, 1.528665f));
        Corner(path, BR);
        path.AddLine(BR(1.528665f, 0f), BL(1.528665f, 0f));
        Corner(path, BL);
        path.AddLine(BL(0f, 1.528665f), TL(0f, 1.528665f));
        Corner(path, TL);
        path.CloseFigure();
        return path;

        static void Corner(GraphicsPath path, Func<float, float, PointF> at)
        {
            path.AddBezier(
                at(1.528665f, 0f), at(1.08849296f, 0f), at(0.86840694f, 0f), at(0.63149379f, 0.07491139f));
            path.AddBezier(
                at(0.63149379f, 0.07491139f), at(0.37282383f, 0.16905956f), at(0.16905956f, 0.37282383f), at(0.07491139f, 0.63149379f));
            path.AddBezier(
                at(0.07491139f, 0.63149379f), at(0f, 0.86840694f), at(0f, 1.08849296f), at(0f, 1.528665f));
        }
    }

    /// <summary>Coverage mask for the squircle, cached per pixel size.</summary>
    private static byte[] SquircleMask(int size)
    {
        lock (MaskGate)
        {
            if (MaskCache.TryGetValue(size, out var cached)) return cached;

            using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = SquirclePath(size);
                using var brush = new SolidBrush(Color.White);
                graphics.FillPath(brush, path);
            }
            var bytes = ReadRgba(bitmap);
            MaskCache[size] = bytes;
            return bytes;
        }
    }

    // ---- compose -----------------------------------------------------------

    private static void FillPlate(byte[] tile, int size, Color plate)
    {
        for (var i = 0; i < size * size; i++)
        {
            var offset = i * 4;
            tile[offset] = plate.R;
            tile[offset + 1] = plate.G;
            tile[offset + 2] = plate.B;
            tile[offset + 3] = 255;
        }
    }

    private static void DrawArtworkFit(
        byte[] artwork,
        int sourceWidth,
        int sourceHeight,
        (int X, int Y, int W, int H) bounds,
        byte[] tile,
        int size,
        bool fullBleed)
    {
        using var source = ToBitmap(artwork, sourceWidth, sourceHeight);
        using var layer = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(layer))
        {
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingMode = CompositingMode.SourceOver;
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);

            if (fullBleed)
            {
                graphics.DrawImage(
                    source,
                    new Rectangle(0, 0, size, size),
                    bounds.X, bounds.Y, bounds.W, bounds.H,
                    GraphicsUnit.Pixel, attributes);
            }
            else
            {
                var box = size - 2 * Math.Round(size * ArtworkInsetFraction);
                var scale = Math.Min(box / bounds.W, box / bounds.H);
                var width = Math.Max(1, (int)Math.Round(bounds.W * scale));
                var height = Math.Max(1, (int)Math.Round(bounds.H * scale));
                graphics.DrawImage(
                    source,
                    new Rectangle((size - width) / 2, (size - height) / 2, width, height),
                    bounds.X, bounds.Y, bounds.W, bounds.H,
                    GraphicsUnit.Pixel, attributes);
            }
        }

        var pixels = ReadRgba(layer);
        for (var i = 0; i < size * size; i++)
        {
            var offset = i * 4;
            var alpha = pixels[offset + 3];
            if (alpha == 0) continue;
            BlendOver(tile, offset, pixels[offset], pixels[offset + 1], pixels[offset + 2], alpha);
        }
    }

    /// <summary>
    /// A compact flat shortcut mark. The glyph flips to a light ink
    /// on a dark seat so the mark stays legible whichever plate the user picks.
    /// </summary>
    private static void DrawBadge(byte[] tile, int size, Color seatColor)
    {
        var seat = Math.Max(size * BadgeSeatFraction, BadgeMinSeatPixels);
        var seatPixels = Math.Max(1, (int)Math.Round(seat));
        var seatX = (int)Math.Round(Math.Max(size * BadgeCornerFraction, 0));
        var seatY = (int)Math.Round(Math.Max(size - seat - size * BadgeCornerFraction, 0));
        var mask = SquircleMask(seatPixels);

        for (var y = 0; y < seatPixels; y++)
        {
            for (var x = 0; x < seatPixels; x++)
            {
                var alpha = mask[(y * seatPixels + x) * 4 + 3];
                if (alpha == 0) continue;
                var targetX = seatX + x;
                var targetY = seatY + y;
                if (targetX < 0 || targetY < 0 || targetX >= size || targetY >= size) continue;
                BlendOver(tile, (targetY * size + targetX) * 4, seatColor.R, seatColor.G, seatColor.B, alpha);
            }
        }

        // The hairline ring is what keeps the seat from melting into a white plate.
        var darkSeat = Luminance(seatColor) <= 0.6;
        var ringColor = darkSeat ? (R: (byte)255, G: (byte)255, B: (byte)255, A: 140)
                                 : (R: (byte)0, G: (byte)0, B: (byte)0, A: 31);
        var inset = Math.Max(1.2, size * 0.008);
        var innerSize = Math.Max(1, (int)Math.Round(seat - 2.0 * inset));
        var innerX = (int)Math.Round(seatX + inset);
        var innerY = (int)Math.Round(seatY + inset);
        var innerMask = SquircleMask(innerSize);
        for (var y = 0; y < seatPixels; y++)
        {
            for (var x = 0; x < seatPixels; x++)
            {
                var outer = mask[(y * seatPixels + x) * 4 + 3];
                if (outer == 0) continue;
                var localX = x - (innerX - seatX);
                var localY = y - (innerY - seatY);
                var inner = localX >= 0 && localY >= 0 && localX < innerSize && localY < innerSize
                    ? innerMask[(localY * innerSize + localX) * 4 + 3]
                    : 0;
                var coverage = Math.Max(0, outer - inner);
                if (coverage == 0) continue;
                var targetX = seatX + x;
                var targetY = seatY + y;
                if (targetX < 0 || targetY < 0 || targetX >= size || targetY >= size) continue;
                BlendOver(
                    tile,
                    (targetY * size + targetX) * 4,
                    ringColor.R, ringColor.G, ringColor.B,
                    coverage * ringColor.A / 255);
            }
        }

        // Arrow glyph: a shaft plus two barbs, drawn as capsules so the joins are
        // round by construction instead of relying on stroke joins.
        var centreX = seatX + seat / 2.0;
        var centreY = seatY + seat / 2.0;
        var radius = seat / 2.0;
        var halfWidth = 0.159;
        var softness = 1.3 / radius;
        var ink = darkSeat ? ((byte)0xF4, (byte)0xF4, (byte)0xF1) : ((byte)0x23, (byte)0x23, (byte)0x28);

        var minX = (int)Math.Floor(centreX - radius);
        var maxX = (int)Math.Ceiling(centreX + radius);
        var minY = (int)Math.Floor(centreY - radius);
        var maxY = (int)Math.Ceiling(centreY + radius);
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                if (x < 0 || y < 0 || x >= size || y >= size) continue;
                var u = (x + 0.5 - centreX) / radius;
                var w = (y + 0.5 - centreY) / radius;
                if (u * u + w * w > 1.05) continue;
                var distance = Math.Min(
                    SegmentDistance(u, w, -0.40, 0.40, 0.34, -0.34),
                    Math.Min(
                        SegmentDistance(u, w, 0.06, -0.42, 0.44, -0.44),
                        SegmentDistance(u, w, 0.44, -0.44, 0.42, -0.06)));
                var coverage = Math.Clamp((halfWidth - distance) / softness + 0.5, 0, 1);
                if (coverage <= 0) continue;
                BlendOver(tile, (y * size + x) * 4, ink.Item1, ink.Item2, ink.Item3, (int)(coverage * 255));
            }
        }
    }

    private static void ClipToMask(byte[] tile, int size, byte[] mask)
    {
        for (var i = 0; i < size * size; i++)
        {
            var offset = i * 4;
            var coverage = mask[offset + 3];
            if (coverage == 255) continue;
            if (coverage == 0)
            {
                tile[offset] = tile[offset + 1] = tile[offset + 2] = tile[offset + 3] = 0;
                continue;
            }
            tile[offset + 3] = (byte)(tile[offset + 3] * coverage / 255);
        }
    }

    // ---- source analysis ---------------------------------------------------

    /// <summary>
    /// Recognizes square tiles inside transparent padding, including tiles whose
    /// rounded corners are already transparent.
    /// </summary>
    private static bool IsFullBleed(byte[] rgba, int width, (int X, int Y, int W, int H) bounds)
    {
        if (Math.Min(bounds.W, bounds.H) < Math.Max(bounds.W, bounds.H) * 0.9) return false;
        var total = bounds.W * bounds.H;
        var opaque = 0;
        for (var y = bounds.Y; y < bounds.Y + bounds.H; y++)
        {
            for (var x = bounds.X; x < bounds.X + bounds.W; x++)
            {
                if (rgba[(y * width + x) * 4 + 3] >= 250) opaque++;
            }
        }
        return opaque >= total * FullBleedCoverage;
    }

    private static (int X, int Y, int W, int H) AlphaBounds(byte[] rgba, int width, int height)
    {
        var minX = width;
        var minY = height;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (rgba[(y * width + x) * 4 + 3] <= 8) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        return maxX < 0 ? (0, 0, width, height) : (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    // ---- pixel plumbing ----------------------------------------------------

    private static Bitmap DownsampleTile(byte[] tile, int work)
    {
        var size = work / 2;
        var output = new byte[size * size * 4];
        // Premultiplied area sampling preserves flat colors at small sizes and
        // prevents transparent corner pixels from bleeding into the outline.
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                int alpha = 0, red = 0, green = 0, blue = 0;
                for (var dy = 0; dy < 2; dy++)
                {
                    for (var dx = 0; dx < 2; dx++)
                    {
                        var offset = ((2 * y + dy) * work + 2 * x + dx) * 4;
                        var a = tile[offset + 3];
                        alpha += a;
                        red += tile[offset] * a;
                        green += tile[offset + 1] * a;
                        blue += tile[offset + 2] * a;
                    }
                }
                if (alpha == 0) continue;
                var target = (y * size + x) * 4;
                output[target] = (byte)((red + alpha / 2) / alpha);
                output[target + 1] = (byte)((green + alpha / 2) / alpha);
                output[target + 2] = (byte)((blue + alpha / 2) / alpha);
                output[target + 3] = (byte)((alpha + 2) / 4);
            }
        }
        return ToBitmap(output, size);
    }

    private static void BlendOver(byte[] destination, int offset, byte r, byte g, byte b, int alpha)
    {
        if (alpha <= 0) return;
        if (alpha >= 255)
        {
            destination[offset] = r;
            destination[offset + 1] = g;
            destination[offset + 2] = b;
            destination[offset + 3] = 255;
            return;
        }

        var sourceAlpha = alpha / 255.0;
        var destinationAlpha = destination[offset + 3] / 255.0;
        var outAlpha = sourceAlpha + destinationAlpha * (1 - sourceAlpha);
        if (outAlpha <= 0)
        {
            destination[offset + 3] = 0;
            return;
        }

        destination[offset] = (byte)Math.Round((r * sourceAlpha + destination[offset] * destinationAlpha * (1 - sourceAlpha)) / outAlpha);
        destination[offset + 1] = (byte)Math.Round((g * sourceAlpha + destination[offset + 1] * destinationAlpha * (1 - sourceAlpha)) / outAlpha);
        destination[offset + 2] = (byte)Math.Round((b * sourceAlpha + destination[offset + 2] * destinationAlpha * (1 - sourceAlpha)) / outAlpha);
        destination[offset + 3] = (byte)Math.Round(outAlpha * 255);
    }

    private static double SegmentDistance(
        double px, double py, double ax, double ay, double bx, double by)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy), 0, 1);
        var qx = ax + t * dx;
        var qy = ay + t * dy;
        return Math.Sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy));
    }

    private static double Luminance(Color color) =>
        (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;

    private static byte[] ReadRgba(Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var buffer = new byte[width * height * 4];
        var data = bitmap.LockBits(
            new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                var row = new byte[data.Stride];
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                    for (var x = 0; x < width; x++)
                    {
                        var source = x * 4;
                        var target = (y * width + x) * 4;
                        buffer[target] = row[source + 2];
                        buffer[target + 1] = row[source + 1];
                        buffer[target + 2] = row[source];
                        buffer[target + 3] = row[source + 3];
                    }
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return buffer;
    }

    private static Bitmap ToBitmap(byte[] rgba, int size) => ToBitmap(rgba, size, size);

    private static Bitmap ToBitmap(byte[] rgba, int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(
            new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                var row = new byte[width * 4];
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var source = (y * width + x) * 4;
                        row[x * 4] = rgba[source + 2];
                        row[x * 4 + 1] = rgba[source + 1];
                        row[x * 4 + 2] = rgba[source];
                        row[x * 4 + 3] = rgba[source + 3];
                    }
                    Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    private static Bitmap Downscale(Bitmap source, int size)
    {
        var scaled = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingMode = CompositingMode.SourceCopy;
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            graphics.DrawImage(
                source,
                new Rectangle(0, 0, size, size),
                0, 0, source.Width, source.Height,
                GraphicsUnit.Pixel,
                attributes);
        }
        return scaled;
    }
}
