using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class AcrylicOverlapBlurTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void OnlyLowerPixelsInsideUpperOutlineAreBlurred(double scale)
    {
        using var bitmap = Stripes((int)(200 * scale), (int)(160 * scale));
        var outside = bitmap.GetPixel((int)(20 * scale), (int)(70 * scale));
        var corner = bitmap.GetPixel((int)(61 * scale), (int)(31 * scale));
        Assert.True(AcrylicOverlapBlur.Apply(bitmap, new(60, 30, 100, 100), 20, scale,
            [new RectangleF(0, 0, 200, 160)]));
        var softened = bitmap.GetPixel((int)(100 * scale), (int)(70 * scale));
        Assert.InRange((int)softened.R, 80, 180);
        Assert.Equal(outside, bitmap.GetPixel((int)(20 * scale), (int)(70 * scale)));
        Assert.Equal(corner, bitmap.GetPixel((int)(61 * scale), (int)(31 * scale)));

        // The current foreground is drawn after filtering, not fed into it.
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.FillRectangle(Brushes.Lime, (int)(100 * scale), (int)(70 * scale), 10, 10);
        Assert.Equal(Color.Lime.ToArgb(), bitmap.GetPixel((int)(104 * scale), (int)(74 * scale)).ToArgb());
    }

    [Fact]
    public void DisjointBoxesDoNotChangeAnyPixels()
    {
        using var bitmap = Stripes(200, 160);
        using var expected = (Bitmap)bitmap.Clone();
        Assert.False(AcrylicOverlapBlur.Apply(bitmap, new(100, 30, 80, 100), 20, 1,
            [new RectangleF(0, 0, 90, 160)]));
        AssertSamePixels(expected, bitmap);
        Assert.False(AcrylicOverlapBlur.Apply(bitmap, new(0, 0, 200, 160), 20, 1, []));
        AssertSamePixels(expected, bitmap);
    }

    [Fact]
    public void UniformTranslucentTintDoesNotAcquireNoiseOrExtraOpacity()
    {
        using var bitmap = DesktopLayerBitmapFactory.Create(200, 160);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.FromArgb(144, 70, 100, 140));
        var expected = bitmap.GetPixel(100, 80);
        Assert.True(AcrylicOverlapBlur.Apply(bitmap, new(40, 20, 120, 120), 12, 1,
            [new RectangleF(0, 0, 200, 160)]));
        for (var y = 40; y < 110; y++)
            for (var x = 60; x < 140; x++)
                Assert.Equal(expected, bitmap.GetPixel(x, y));
    }

    [Fact]
    public void TranslucentEdgesUsePremultipliedColorWithoutBlackFringes()
    {
        using var bitmap = DesktopLayerBitmapFactory.Create(200, 160);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.FromArgb(160, Color.Red));
            graphics.FillRectangle(brush, 0, 0, 100, 160);
        }
        Assert.True(AcrylicOverlapBlur.Apply(bitmap, new(40, 20, 120, 120), 12, 1,
            [new RectangleF(0, 0, 100, 160)]));
        var edge = bitmap.GetPixel(100, 80);
        Assert.InRange((int)edge.A, 1, 159);
        Assert.InRange((int)edge.R, 250, 255);
        Assert.Equal(0, edge.G);
        Assert.Equal(0, edge.B);
        Assert.Equal(0, bitmap.GetPixel(155, 80).A);
    }

    [Theory]
    [InlineData(-40, -30)]
    [InlineData(140, 110)]
    public void PartiallyOffscreenBoxIsClippedToCanvas(int x, int y)
    {
        using var bitmap = Stripes(200, 160);
        Assert.True(AcrylicOverlapBlur.Apply(bitmap, new(x, y, 100, 100), 12, 1,
            [new RectangleF(0, 0, 200, 160)]));
    }

    private static Bitmap Stripes(int width, int height)
    {
        var bitmap = DesktopLayerBitmapFactory.Create(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Black);
        for (var x = 0; x < width; x += 8) graphics.FillRectangle(Brushes.White, x, 0, 4, height);
        return bitmap;
    }

    private static void AssertSamePixels(Bitmap expected, Bitmap actual)
    {
        for (var y = 0; y < actual.Height; y++)
            for (var x = 0; x < actual.Width; x++)
                Assert.Equal(expected.GetPixel(x, y), actual.GetPixel(x, y));
    }
}
