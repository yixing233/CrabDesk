using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using CrabDesk.Native;

namespace CrabDesk.Tests;

/// <summary>
/// The flat skin is a pure pixel transform. These tests pin the properties that
/// matter if it ever stops being one: the source is never mutated, transparency
/// outside the squircle is genuinely transparent, and a skin failure cannot lose
/// the icon.
/// </summary>
public class FlatIconSkinTests
{
    private static Bitmap SolidSquare(int size, Color color)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(color);
        return bitmap;
    }

    private static Bitmap BareLogo(int size, Color color)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        using var brush = new SolidBrush(color);
        graphics.FillEllipse(brush, size / 4f, size / 4f, size / 2f, size / 2f);
        return bitmap;
    }

    private static byte[] ReadPixels(Bitmap bitmap)
    {
        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[bitmap.Width * bitmap.Height * 4];
            var row = new byte[data.Stride];
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                Array.Copy(row, 0, bytes, y * bitmap.Width * 4, bitmap.Width * 4);
            }
            return bytes;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static readonly IconSkinOptions Flat = IconSkinOptions.Disabled with { Enabled = true };

    [Fact]
    public void CornersAreTransparentBecauseThePlateIsASquircle()
    {
        using var source = BareLogo(256, Color.CornflowerBlue);
        using var tile = FlatIconSkin.Render(source, 96, IconSkinKind.App, Flat);

        Assert.Equal(96, tile.Width);
        Assert.Equal(96, tile.Height);
        Assert.Equal(0, tile.GetPixel(0, 0).A);
        Assert.Equal(0, tile.GetPixel(95, 0).A);
        Assert.Equal(0, tile.GetPixel(0, 95).A);
        Assert.Equal(0, tile.GetPixel(95, 95).A);
        // The centre of the plate must be opaque, or the shape ate the icon.
        Assert.Equal(255, tile.GetPixel(48, 14).A);
    }

    [Fact]
    public void TheSourceArtworkIsNeverModified()
    {
        using var source = SolidSquare(64, Color.OrangeRed);
        var before = ReadPixels(source);

        using var _ = FlatIconSkin.Render(source, 96, IconSkinKind.App, Flat);

        Assert.Equal(before, ReadPixels(source));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(96)]
    [InlineData(256)]
    public void EverySupportedSizeRendersAtTheRequestedEdge(int size)
    {
        using var source = BareLogo(256, Color.SeaGreen);
        using var tile = FlatIconSkin.Render(source, size, IconSkinKind.File, Flat);
        Assert.Equal(size, tile.Width);
        Assert.Equal(size, tile.Height);
    }

    [Fact]
    public void SmallIconsKeepTheirArtworkLegible()
    {
        using var source = BareLogo(256, Color.SeaGreen);
        using var app = FlatIconSkin.Render(source, 16, IconSkinKind.App, Flat);
        using var shortcut = FlatIconSkin.Render(source, 16, IconSkinKind.Shortcut, Flat);

        Assert.Equal(ReadPixels(app), ReadPixels(shortcut));
        Assert.Equal(Color.SeaGreen.ToArgb(), shortcut.GetPixel(8, 8).ToArgb());
    }

    [Fact]
    public void APureSquareIconIsCarvedInsteadOfBeingDoubleFramed()
    {
        // A full-bleed photo already owns its edges; plating it would show a
        // second frame, so the corners must come from the artwork, not a plate.
        using var source = SolidSquare(256, Color.DarkSlateBlue);
        using var tile = FlatIconSkin.Render(source, 96, IconSkinKind.App, Flat);

        var corner = tile.GetPixel(1, 1);
        Assert.True(corner.A < 40, $"expected the corner to be carved away, alpha was {corner.A}");
        // No white plate may leak into a mid-right edge pixel either.
        var edge = tile.GetPixel(93, 48);
        Assert.True(edge.B > edge.R, $"expected the artwork colour at the edge, got {edge}");
    }

    [Fact]
    public void TheDisabledSkinIsAPlainPassThrough()
    {
        using var source = BareLogo(128, Color.Goldenrod);
        using var tile = FlatIconSkin.Render(source, 64, IconSkinKind.App, IconSkinOptions.Disabled);

        // Skipping the skin must not add a plate: the corners are still
        // transparent and the artwork keeps its own silhouette.
        Assert.Equal(0, tile.GetPixel(0, 0).A);
        Assert.Equal(0, tile.GetPixel(8, 8).A);
        Assert.True(tile.GetPixel(32, 32).A > 200);
    }

    [Fact]
    public void DifferentKindsProduceDifferentPixels()
    {
        using var source = BareLogo(256, Color.MediumPurple);
        using var app = FlatIconSkin.Render(source, 96, IconSkinKind.App, Flat);
        using var folder = FlatIconSkin.Render(source, 96, IconSkinKind.Folder, Flat);

        // A flat plate region near the top must differ between the white and the
        // porcelain plate — this is what distinguishes "程序" from "文件夹".
        Assert.NotEqual(app.GetPixel(48, 12).ToArgb(), folder.GetPixel(48, 12).ToArgb());
    }

    [Fact]
    public void ACustomBadgeColourReachesTheSeat()
    {
        using var source = BareLogo(256, Color.SeaShell);
        var dark = Flat with { BadgeColor = Color.FromArgb(0x23, 0x23, 0x28) };
        using var whiteSeat = FlatIconSkin.Render(source, 96, IconSkinKind.Shortcut, Flat);
        using var darkSeat = FlatIconSkin.Render(source, 96, IconSkinKind.Shortcut, dark);

        // The seat is roughly the bottom-left 20% of the tile. Counting dark
        // pixels there is robust against the glyph sitting on top of any single
        // sample point.
        static int DarkPixels(Bitmap tile)
        {
            var count = 0;
            for (var y = 66; y < 94; y++)
            {
                for (var x = 4; x < 32; x++)
                {
                    if (tile.GetPixel(x, y).GetBrightness() < 0.35f) count++;
                }
            }
            return count;
        }

        Assert.True(
            DarkPixels(darkSeat) > DarkPixels(whiteSeat) + 100,
            $"a dark badge must darken the seat (white={DarkPixels(whiteSeat)}, dark={DarkPixels(darkSeat)})");
    }

    [Fact]
    public void TheCacheStampChangesWithEveryVisualInput()
    {
        var baseline = (IconSkinOptions.Disabled with { Enabled = true }).CacheStamp();
        Assert.NotEqual(baseline, (Flat with { AppPlate = Color.Black }).CacheStamp());
        Assert.NotEqual(baseline, (Flat with { FolderPlate = Color.Black }).CacheStamp());
        Assert.NotEqual(baseline, (Flat with { BadgeColor = Color.Black }).CacheStamp());
        Assert.NotEqual(baseline, (Flat with { ShowShortcutArrow = false }).CacheStamp());
        Assert.NotEqual(IconSkinOptions.Disabled.CacheStamp(),
            (IconSkinOptions.Disabled with { ShowShortcutArrow = false }).CacheStamp());
        Assert.NotEqual(baseline, IconSkinOptions.Disabled.CacheStamp());
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(96)]
    public void HidingTheShortcutArrowPreservesTheApplicationArtwork(int size)
    {
        using var source = BareLogo(256, Color.CornflowerBlue);
        using var app = FlatIconSkin.Render(source, size, IconSkinKind.App, Flat);
        using var shortcut = FlatIconSkin.Render(source, size, IconSkinKind.Shortcut,
            Flat with { ShowShortcutArrow = false });

        Assert.Equal(ReadPixels(app), ReadPixels(shortcut));
    }

    [Theory]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(96)]
    public void PlateAndArtworkKeepTheirExactColors(int size)
    {
        using var source = BareLogo(256, Color.CornflowerBlue);
        var options = Flat with { AppPlate = Color.FromArgb(240, 245, 250) };
        using var tile = FlatIconSkin.Render(source, size, IconSkinKind.App, options);

        Assert.Equal(options.AppPlate.ToArgb(), tile.GetPixel(size / 2, size / 10).ToArgb());
        Assert.Equal(options.AppPlate.ToArgb(), tile.GetPixel(size / 2, size * 9 / 10).ToArgb());
        Assert.Equal(Color.CornflowerBlue.ToArgb(), tile.GetPixel(size / 2, size / 2).ToArgb());
    }

    [Theory]
    [InlineData(IconSkinKind.App)]
    [InlineData(IconSkinKind.Folder)]
    [InlineData(IconSkinKind.File)]
    public void OnlyShortcutsUseTheBadgeColor(IconSkinKind kind)
    {
        using var source = BareLogo(256, Color.CornflowerBlue);
        using var first = FlatIconSkin.Render(source, 96, kind, Flat);
        using var second = FlatIconSkin.Render(source, 96, kind, Flat with { BadgeColor = Color.Black });

        Assert.Equal(ReadPixels(first), ReadPixels(second));
    }

    [Fact]
    public void ShortcutUsesTheApplicationPlate()
    {
        using var source = BareLogo(256, Color.CornflowerBlue);
        using var tile = FlatIconSkin.Render(source, 96, IconSkinKind.Shortcut, Flat);

        Assert.Equal(Flat.AppPlate.ToArgb(), tile.GetPixel(48, 12).ToArgb());
    }

    [Fact]
    public void PaddedRoundedTilesKeepTheirOwnBackground()
    {
        using var source = new Bitmap(128, 128, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(source))
        {
            graphics.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.RoyalBlue);
            graphics.FillRectangle(brush, 24, 16, 80, 96);
            graphics.FillRectangle(brush, 16, 24, 96, 80);
            graphics.FillEllipse(brush, 16, 16, 16, 16);
            graphics.FillEllipse(brush, 96, 16, 16, 16);
            graphics.FillEllipse(brush, 16, 96, 16, 16);
            graphics.FillEllipse(brush, 96, 96, 16, 16);
        }
        using var tile = FlatIconSkin.Render(source, 96, IconSkinKind.App, Flat);

        Assert.Equal(Color.RoyalBlue.ToArgb(), tile.GetPixel(48, 8).ToArgb());
        Assert.Equal(Color.RoyalBlue.ToArgb(), tile.GetPixel(8, 48).ToArgb());
        Assert.Equal(0, tile.GetPixel(0, 0).A);
    }

    [Fact]
    public void TallArtworkPreservesItsAspectRatioAndPlate()
    {
        using var source = new Bitmap(128, 128, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(source))
        {
            graphics.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.CornflowerBlue);
            graphics.FillRectangle(brush, 48, 8, 32, 112);
        }
        using var tile = FlatIconSkin.Render(source, 96, IconSkinKind.File, Flat);

        Assert.Equal(0, tile.GetPixel(12, 48).A);
        Assert.Equal(Flat.FolderPlate.ToArgb(), tile.GetPixel(20, 48).ToArgb());
        Assert.Equal(Color.CornflowerBlue.ToArgb(), tile.GetPixel(48, 48).ToArgb());
    }
}
