using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class WidgetCenterChromeTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    [InlineData(14f)]
    [InlineData(32f)]
    [InlineData(100f)]
    [InlineData(240f)]
    [InlineData(320f)]
    [InlineData(420f)]
    [InlineData(760f)]
    public void HeaderGeometryStaysInsideTheSurfaceWithoutOverlapping(float width)
    {
        var add = WidgetCenterChrome.AddBounds(width);
        var title = WidgetCenterChrome.TitleBounds(width);
        foreach (var bounds in new[] { add, title })
        {
            Assert.InRange(bounds.Left, 0, width);
            Assert.InRange(bounds.Right, 0, width);
            Assert.InRange(bounds.Top, 0, WidgetCenterChrome.HeaderHeight);
            Assert.InRange(bounds.Bottom, 0, WidgetCenterChrome.HeaderHeight);
        }
        Assert.False(title.IntersectsWith(add));
    }

    [Fact]
    public void HeaderActionsHaveStableSizesAndReserveTitleSpace()
    {
        var add = WidgetCenterChrome.AddBounds(520);
        var title = WidgetCenterChrome.TitleBounds(520);
        Assert.Equal(new SizeF(116, 36), add.Size);
        Assert.Equal(16, 520 - add.Right);
        Assert.True(title.Width >= 240);
        Assert.True(title.Right + 12 <= add.Left);
        Assert.Equal(add.Size, WidgetCenterChrome.AddBounds(760).Size);
        Assert.Equal(add.Left + 240, WidgetCenterChrome.AddBounds(760).Left);
    }

    [Theory]
    [InlineData(760f, 600f)]
    [InlineData(14f, 120f)]
    [InlineData(1f, 100f)]
    [InlineData(760f, 80f)]
    [InlineData(0f, 600f)]
    [InlineData(760f, 0f)]
    public void ScrollTrackFitsEvenSmallSurfaces(float width, float height)
    {
        var track = WidgetCenterChrome.ScrollTrack(width, height);
        Assert.InRange(track.Left, 0, width);
        Assert.InRange(track.Right, 0, width);
        Assert.InRange(track.Top, 0, height);
        Assert.InRange(track.Bottom, 0, height);
        Assert.InRange(track.Width, 0, 14);
        if (!track.IsEmpty) Assert.True(track.Top >= WidgetCenterChrome.HeaderHeight);
    }

    [Fact]
    public void ScrollTrackReservesFourteenDipWithoutPaintingAFullTrack()
    {
        Assert.Equal(new RectangleF(738, 72, 14, 512), WidgetCenterChrome.ScrollTrack(760, 600));
    }

    [Theory]
    [InlineData(100d, 400d, -10d, 0d)]
    [InlineData(100d, 400d, 75d, 75d)]
    [InlineData(100d, 400d, 500d, 300d)]
    [InlineData(100d, 100d, 75d, 0d)]
    [InlineData(100d, 50d, 75d, 0d)]
    [InlineData(0d, 400d, 75d, 0d)]
    [InlineData(-1d, 400d, 75d, 0d)]
    public void ScrollOffsetIsClampedToTheMeasuredRange(double viewport, double content, double offset, double expected)
    {
        Assert.Equal(expected, WidgetCenterChrome.ClampScrollOffset(viewport, content, offset));
        Assert.Equal(viewport > 0 && content > viewport ? content - viewport : 0,
            WidgetCenterChrome.MaximumScrollOffset(viewport, content));
    }

    [Fact]
    public void InvalidMeasurementsAndOffsetsProduceFiniteGeometry()
    {
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1d, 0d })
        {
            Assert.Equal(0, WidgetCenterChrome.MaximumScrollOffset(invalid, 400));
            Assert.Equal(0, WidgetCenterChrome.MaximumScrollOffset(100, invalid));
            Assert.Equal(RectangleF.Empty, WidgetCenterChrome.ScrollThumb(new RectangleF(0, 0, 14, 200), invalid, 400, 0));
            Assert.Equal(RectangleF.Empty, WidgetCenterChrome.ScrollThumb(new RectangleF(0, 0, 14, 200), 100, invalid, 0));
        }
        Assert.Equal(0, WidgetCenterChrome.ClampScrollOffset(100, 400, double.NaN));
        Assert.Equal(0, WidgetCenterChrome.ClampScrollOffset(100, 400, double.NegativeInfinity));
        Assert.Equal(300, WidgetCenterChrome.ClampScrollOffset(100, 400, double.PositiveInfinity));
        Assert.Equal(RectangleF.Empty, WidgetCenterChrome.ScrollThumb(new RectangleF(float.NaN, 0, 14, 200), 100, 400, 0));
        Assert.Equal(RectangleF.Empty, WidgetCenterChrome.ScrollThumb(RectangleF.Empty, 100, 400, 0));
        Assert.Equal(RectangleF.Empty, WidgetCenterChrome.AddBounds(float.NaN));
        Assert.Equal(RectangleF.Empty, WidgetCenterChrome.AddBounds(float.PositiveInfinity));
        Assert.Equal(RectangleF.Empty, WidgetCenterChrome.ScrollTrack(760, float.NaN));
    }

    [Fact]
    public void ScrollThumbUsesTheViewportRatioAndClampsAtBothEnds()
    {
        var track = new RectangleF(738, 72, 14, 512);
        var top = WidgetCenterChrome.ScrollThumb(track, 100, 400, -1);
        var middle = WidgetCenterChrome.ScrollThumb(track, 100, 400, 150);
        var bottom = WidgetCenterChrome.ScrollThumb(track, 100, 400, 900);
        Assert.Equal(new RectangleF(743, 72, 4, 128), top);
        Assert.Equal(264, middle.Top);
        Assert.Equal(track.Bottom, bottom.Bottom);
        var expanded = WidgetCenterChrome.ScrollThumb(track, 100, 400, 150, true);
        Assert.Equal(6, expanded.Width);
        Assert.Equal(middle.Top, expanded.Top);
        Assert.Equal(middle.Height, expanded.Height);
        Assert.Equal(middle.Left + middle.Width / 2, expanded.Left + expanded.Width / 2);
        Assert.Equal(RectangleF.Empty, WidgetCenterChrome.ScrollThumb(track, 400, 400, 0));
        Assert.Equal(RectangleF.Empty, WidgetCenterChrome.ScrollThumb(track, 500, 400, 0));
    }

    [Theory]
    [InlineData(512f, 14f, false)]
    [InlineData(512f, 14f, true)]
    [InlineData(16f, 14f, true)]
    [InlineData(28f, 3f, true)]
    [InlineData(0.5f, 0.5f, false)]
    public void MinimumThumbNeverOverflowsTheTrack(float height, float width, bool expanded)
    {
        var track = new RectangleF(10, 20, width, height);
        foreach (var offset in new[] { -100d, 0d, 50000d, double.PositiveInfinity })
        {
            var thumb = WidgetCenterChrome.ScrollThumb(track, 1, 100000, offset, expanded);
            Assert.Equal(Math.Min(28, height), thumb.Height);
            Assert.InRange(thumb.Left, track.Left, track.Right);
            Assert.InRange(thumb.Right, track.Left, track.Right);
            Assert.InRange(thumb.Top, track.Top, track.Bottom);
            Assert.InRange(thumb.Bottom, track.Top, track.Bottom);
            Assert.Equal(Math.Min(width, expanded ? 6 : 4), thumb.Width);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeaderKeepsTransparentCornersAndGrayscalePremultipliedText(bool pickerVisible) => InSta(() =>
    {
        using var chrome = new WidgetCenterChrome();
        using var bitmap = new Bitmap(760, 80, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        chrome.DrawHeader(graphics, 760, Color.White, pickerVisible, true, false, false);
        var add = WidgetCenterChrome.AddBounds(760);
        Assert.Equal(0, bitmap.GetPixel((int)add.Left, (int)add.Top).A);
        Assert.InRange(bitmap.GetPixel((int)add.Left + 12, (int)add.Top + 4).A, 1, 254);
        Assert.Equal(0, bitmap.GetPixel(0, 32).A);
        Assert.Equal(0, bitmap.GetPixel(300, 60).A);
        Assert.Equal(0, bitmap.GetPixel(300, 70).A);
        var title = Rectangle.Round(WidgetCenterChrome.TitleBounds(760));
        var titleHasInk = false;
        var titleHasAntialiasing = false;
        for (var y = title.Top; y < title.Bottom; y++)
        for (var x = title.Left; x < title.Right; x++)
        {
            var alpha = bitmap.GetPixel(x, y).A;
            titleHasInk |= alpha > 0;
            titleHasAntialiasing |= alpha > 0 && alpha < 255;
        }
        Assert.True(titleHasInk);
        Assert.True(titleHasAntialiasing);
        var pixels = Pixels(bitmap);
        for (var index = 0; index < pixels.Length; index += 4)
        {
            Assert.Equal(pixels[index], pixels[index + 1]);
            Assert.Equal(pixels[index], pixels[index + 2]);
            Assert.True(pixels[index] <= pixels[index + 3]);
        }
    });

    [Fact]
    public void HeaderInteractionFillsRemainTranslucentAndPressedIsStronger() => InSta(() =>
    {
        using var chrome = new WidgetCenterChrome();
        var samples = new List<byte>();
        foreach (var state in new[] { (false, false), (true, false), (true, true) })
        {
            using var bitmap = new Bitmap(520, 64, PixelFormat.Format32bppPArgb);
            using var graphics = Graphics.FromImage(bitmap);
            chrome.DrawHeader(graphics, 520, Color.White, false, state.Item1, state.Item2, false);
            var bounds = WidgetCenterChrome.AddBounds(520);
            samples.Add(bitmap.GetPixel((int)bounds.Left + 12, (int)bounds.Top + 4).A);
        }
        Assert.True(samples[0] > 0 && samples[0] < samples[1] && samples[1] < samples[2] && samples[2] < 255);
    });

    [Theory]
    [InlineData(1f)]
    [InlineData(1.25f)]
    [InlineData(2f)]
    public void HeaderCacheIsRepeatableAndRestoresGraphicsState(float scale) => InSta(() =>
    {
        using var chrome = new WidgetCenterChrome();
        byte[] Render(Color color)
        {
            using var bitmap = new Bitmap((int)(520 * scale), (int)(80 * scale), PixelFormat.Format32bppPArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.ScaleTransform(scale, scale);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.SmoothingMode = SmoothingMode.None;
            using var transform = graphics.Transform;
            chrome.DrawHeader(graphics, 520, color, true, false, false, true);
            Assert.Equal(CompositingMode.SourceCopy, graphics.CompositingMode);
            Assert.Equal(SmoothingMode.None, graphics.SmoothingMode);
            using var after = graphics.Transform;
            Assert.Equal(transform.Elements, after.Elements);
            return Pixels(bitmap);
        }
        var first = Render(Color.White);
        Assert.Equal(first, Render(Color.White));
        Assert.False(first.SequenceEqual(Render(Color.Black)));
        Assert.Equal(first, Render(Color.White));
    });

    [Fact]
    public void ScrollPaintOnlyTouchesTheRoundedTranslucentThumb()
    {
        using var chrome = new WidgetCenterChrome();
        var track = new RectangleF(10, 10, 14, 180);
        using var bitmap = new Bitmap(40, 210, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        chrome.DrawScroll(graphics, track, 100, 400, 0, Color.White, false, false);
        var thumb = WidgetCenterChrome.ScrollThumb(track, 100, 400, 0);
        Assert.InRange(bitmap.GetPixel((int)(thumb.Left + thumb.Width / 2), (int)(thumb.Top + 10)).A, 1, 254);
        Assert.Equal(0, bitmap.GetPixel((int)track.Left, (int)track.Top + 10).A);
        Assert.True(bitmap.GetPixel((int)thumb.Left, (int)thumb.Top).A <
            bitmap.GetPixel((int)(thumb.Left + thumb.Width / 2), (int)(thumb.Top + 10)).A);
        Assert.Equal(0, bitmap.GetPixel((int)thumb.Left + 2, (int)track.Bottom - 5).A);
        var painted = Pixels(bitmap);
        chrome.DrawScroll(graphics, track, 400, 400, 0, Color.White, true, true);
        Assert.Equal(painted, Pixels(bitmap));
    }

    [Fact]
    public void SharedTextRasterCentersClipsAndReusesLabels() => InSta(() =>
    {
        using var chrome = new WidgetCenterChrome();
        byte[] Render()
        {
            using var bitmap = new Bitmap(240, 80, PixelFormat.Format32bppPArgb);
            using var graphics = Graphics.FromImage(bitmap);
            var bounds = new RectangleF(20, 10, 200, 40);
            chrome.DrawText(graphics, "暂无组件", bounds, Color.White, 13, true, true);
            var ink = Rectangle.Empty;
            for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).A == 0) continue;
                Assert.True(bounds.Contains(x, y));
                var pixel = new Rectangle(x, y, 1, 1);
                ink = ink.IsEmpty ? pixel : Rectangle.Union(ink, pixel);
            }
            Assert.False(ink.IsEmpty);
            Assert.InRange(ink.Left + ink.Width / 2f, 116, 124);
            return Pixels(bitmap);
        }
        Assert.Equal(Render(), Render());
    });

    [Theory]
    [InlineData(1f)]
    [InlineData(1.25f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void TextRasterKeepsItsPixelsWhenTheOriginMovesWithinOneDevicePixel(float scale) => InSta(() =>
    {
        using var chrome = new WidgetCenterChrome();
        byte[] Render(float offset)
        {
            using var bitmap = new Bitmap((int)(240 * scale), (int)(80 * scale), PixelFormat.Format32bppPArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.ScaleTransform(scale, scale);
            chrome.DrawText(graphics, " 添加组件", new(20 + offset, 10, 200, 36), Color.White);
            return Pixels(bitmap);
        }
        Assert.Equal(Render(0), Render(.2f / scale));
    });

    private static byte[] Pixels(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try { var bytes = new byte[data.Stride * bitmap.Height]; Marshal.Copy(data.Scan0, bytes, 0, bytes.Length); return bytes; }
        finally { bitmap.UnlockBits(data); }
    }

    private static void InSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception caught) { error = caught; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
