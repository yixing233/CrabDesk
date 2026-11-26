using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.ExceptionServices;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopBoxTypographyTests
{
    [Theory]
    [InlineData(1f)]
    [InlineData(1.25f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void TransparentTextHasNeutralSmoothEdgesAndIdenticalCachedPixels(float scale) => OnSta(() =>
    {
        using var renderer = new DesktopBoxTextRenderer();
        using var font = new Font("Microsoft YaHei UI", 8.5f);
        using var format = new StringFormat { FormatFlags = StringFormatFlags.NoWrap };
        using var first = Render(renderer, font, format, scale, TextRenderingHint.ClearTypeGridFit);
        using var cached = Render(renderer, font, format, scale, TextRenderingHint.AntiAliasGridFit);
        var smooth = 0;
        for (var y = 0; y < first.Height; y++)
        for (var x = 0; x < first.Width; x++)
        {
            var pixel = first.GetPixel(x, y);
            Assert.Equal(pixel, cached.GetPixel(x, y));
            Assert.Equal(pixel.R, pixel.G);
            Assert.Equal(pixel.G, pixel.B);
            if (pixel.A is > 0 and < 255) smooth++;
        }
        Assert.True(smooth > 100, $"Missing alpha coverage at {scale}: {smooth}");
    });

    [Theory]
    [InlineData(1f)]
    [InlineData(1.25f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void FractionalScrollTranslationDoesNotResampleGlyphsAndPreservesClip(float scale) => OnSta(() =>
    {
        using var renderer = new DesktopBoxTextRenderer();
        using var font = new Font("Microsoft YaHei UI", 11);
        using var format = new StringFormat { FormatFlags = StringFormatFlags.NoWrap };
        using var original = Render(renderer, font, format, scale, TextRenderingHint.ClearTypeGridFit);
        using var shifted = Render(renderer, font, format, scale, TextRenderingHint.ClearTypeGridFit, 7.2f);
        for (var y = 7; y < original.Height; y++)
        for (var x = 0; x < original.Width; x++)
            Assert.Equal(original.GetPixel(x, y - 7), shifted.GetPixel(x, y));

        using var clipped = DesktopLayerBitmapFactory.Create(500, 160);
        using var graphics = Graphics.FromImage(clipped);
        graphics.ScaleTransform(scale, scale);
        graphics.SetClip(new RectangleF(0, 0, 45 / scale, 160 / scale));
        renderer.Draw(graphics, "工具 Tools", font, Color.White, new(8, 8, 220, 50), format);
        for (var y = 0; y < clipped.Height; y++)
        for (var x = 45; x < clipped.Width; x++) Assert.Equal(0, clipped.GetPixel(x, y).A);
    });

    [Fact]
    public void FilenameLayoutsKeepOneTwoAndFullLinesAndHonorFontChanges() => OnSta(() =>
    {
        const string value = "很长的中文文件名称 Microsoft Word presentation report.docx";
        using var bitmap = DesktopLayerBitmapFactory.Create(200, 200);
        using var graphics = Graphics.FromImage(bitmap);
        using var font = new Font("Microsoft YaHei UI", 8.5f);
        using var larger = new Font("Segoe UI", 14, FontStyle.Bold);
        using var single = new StringFormat { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
        using var compact = new StringFormat { FormatFlags = StringFormatFlags.LineLimit, Trimming = StringTrimming.EllipsisCharacter };
        using var full = new StringFormat { FormatFlags = StringFormatFlags.LineLimit, Trimming = StringTrimming.None };
        var one = DesktopBoxTextRenderer.Measure(graphics, value, font, new(80, 1000), single);
        var two = DesktopBoxTextRenderer.Measure(graphics, value, font, new(80, 1000), compact);
        var all = DesktopBoxTextRenderer.Measure(graphics, value, font, new(80, 1000), full);
        Assert.Equal(one.Height * 2, two.Height, 2);
        Assert.True(all.Height > two.Height * 2);
        Assert.True(DesktopBoxTextRenderer.Measure(graphics, value, larger, new(80, 1000), single).Height > one.Height);

        using var renderer = new DesktopBoxTextRenderer();
        using var smallPixels = Render(renderer, font, single, 1, TextRenderingHint.ClearTypeGridFit);
        using var largePixels = Render(renderer, larger, single, 1, TextRenderingHint.ClearTypeGridFit);
        Assert.NotEqual(Pixels(smallPixels), Pixels(largePixels));
        using var smallAgain = Render(renderer, font, single, 1, TextRenderingHint.ClearTypeGridFit);
        Assert.Equal(Pixels(smallPixels), Pixels(smallAgain));
    });

    [Fact]
    public void TypographyPreviewShowsRealRendererOnLightAndDarkSurfaces() => OnSta(() =>
    {
        using var renderer = new DesktopBoxTextRenderer();
        using var sheet = DesktopLayerBitmapFactory.Create(960, 660);
        using var output = Graphics.FromImage(sheet);
        using var font = new Font("Microsoft YaHei UI", 8.5f);
        using var title = new Font("Microsoft YaHei UI", 11);
        using var grid = new StringFormat { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.LineLimit };
        using var line = new StringFormat { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
        for (var column = 0; column < 2; column++)
        for (var row = 0; row < 2; row++)
        {
            var scale = row == 0 ? 1f : 1.5f;
            var ink = column == 0 ? Color.White : Color.FromArgb(32, 32, 32);
            using var background = new SolidBrush(column == 0 ? Color.FromArgb(22, 33, 44) : Color.FromArgb(235, 238, 242));
            output.FillRectangle(background, column * 480, row * 330, 480, 330);
            var state = output.Save();
            output.TranslateTransform(column * 480 + 20, row * 330 + 20);
            output.ScaleTransform(scale, scale);
            renderer.Draw(output, $"工具  专业应用 / {scale * 100:0}%", title, ink, new(0, 0, 280, 30), line);
            renderer.Draw(output, "微信开发者工具", font, ink, new(0, 48, 80, 45), grid);
            renderer.Draw(output, "很长的中文文件名称 Microsoft Word.docx", font, ink, new(100, 48, 80, 45), grid);
            renderer.Draw(output, "Microsoft Edge", font, ink, new(0, 112, 200, 26), line);
            renderer.Draw(output, "演示文件 Report.docx", font, ink, new(0, 144, 200, 26), line);
            output.Restore(state);
        }
        sheet.Save(Path.Combine(Path.GetTempPath(), "CrabDesk-Box-Typography.png"), ImageFormat.Png);
    });

    private static Bitmap Render(DesktopBoxTextRenderer renderer, Font font, StringFormat format, float scale, TextRenderingHint hint, float shift = 0)
    {
        var bitmap = DesktopLayerBitmapFactory.Create(500, 160);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Transform = new Matrix(scale, 0, 0, scale, 0, shift);
        graphics.TextRenderingHint = hint;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        renderer.Draw(graphics, "工具 微信开发者 Tools 2026", font, Color.White, new(8, 8, 220, 50), format);
        Assert.Equal(hint, graphics.TextRenderingHint);
        Assert.Equal(InterpolationMode.HighQualityBicubic, graphics.InterpolationMode);
        Assert.Equal(shift, graphics.Transform.OffsetY);
        return bitmap;
    }

    private static int[] Pixels(Bitmap bitmap) => Enumerable.Range(0, bitmap.Height)
        .SelectMany(y => Enumerable.Range(0, bitmap.Width).Select(x => bitmap.GetPixel(x, y).ToArgb())).ToArray();

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { error = exception; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
