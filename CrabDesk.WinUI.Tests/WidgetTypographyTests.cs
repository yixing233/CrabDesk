using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.ExceptionServices;
using CrabDesk.Core;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class WidgetTypographyTests
{
    [Theory]
    [InlineData(1d, false)]
    [InlineData(1.25d, false)]
    [InlineData(1.5d, false)]
    [InlineData(1d, true)]
    [InlineData(1.25d, true)]
    [InlineData(1.5d, true)]
    public void TransparentWidgetTextKeepsSmoothAlphaEdgesAtMonitorDpi(double scale, bool speed)
    {
        using var bitmap = DesktopLayerBitmapFactory.Create(300, 90);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.ScaleTransform((float)scale, (float)scale);
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using (WidgetTextRendering.Begin(graphics, new DesktopWidget { Kind = DesktopWidgetKind.Network }))
        {
            if (speed) DesktopBoxForm.DrawSpeedValue(graphics, 737 * 1024, new(4, 4, 180, 40), Color.Black, 18);
            else typeof(DesktopBoxForm).GetMethod("WidgetText", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [graphics, "网络监控 737.2 KB/s", new RectangleF(4, 4, 180, 40), Color.Black, 18f, true, false, false, false, false]);
        }
        var smooth = 0;
        var solid = 0;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var alpha = bitmap.GetPixel(x, y).A;
                if (alpha is > 0 and < 255) smooth++;
                if (alpha == 255) solid++;
            }
        Assert.True(smooth > 50, $"Missing antialias coverage at {scale}: {smooth}");
        Assert.True(solid > 0);
        Assert.Equal(TextRenderingHint.ClearTypeGridFit, graphics.TextRenderingHint);
    }

    [Fact]
    public void FontSelectionAppliesToMeasurementAndRestoresAcrossWidgets()
    {
        using var bitmap = DesktopLayerBitmapFactory.Create(300, 100);
        using var graphics = Graphics.FromImage(bitmap);
        var widget = new DesktopWidget { FontFamily = "Segoe UI", FontScale = 1.25, FontWeight = 400, FontItalic = true };
        var before = DesktopBoxForm.MeasureWidgetText(graphics, "Network", 18, true);
        using (WidgetTextRendering.Begin(graphics, widget))
        {
            using var font = WidgetTextRendering.CreateFont(graphics, 18, bold: true);
            Assert.Equal("Segoe UI", font.Name);
            Assert.Equal(22.5f, font.Size);
            Assert.False(font.Bold);
            Assert.True(font.Italic);
            Assert.NotEqual(before, DesktopBoxForm.MeasureWidgetText(graphics, "Network", 18, true));
            using (WidgetTextRendering.Begin(graphics, new DesktopWidget { FontWeight = 700 }))
            {
                using var nested = WidgetTextRendering.CreateFont(graphics, 18);
                Assert.True(nested.Bold);
                Assert.Equal(18, nested.Size);
            }
            Assert.Same(widget, WidgetTextRendering.Current(graphics));
        }
        Assert.Null(WidgetTextRendering.Current(graphics));
        Assert.Equal(before, DesktopBoxForm.MeasureWidgetText(graphics, "Network", 18, true));
    }

    [Fact]
    public void WpfWidgetTextRebuildsWhenTypographyChanges() => OnSta(() =>
    {
        using var bitmap = DesktopLayerBitmapFactory.Create(400, 140);
        using var graphics = Graphics.FromImage(bitmap);
        using var visual = new DesktopWidgetTextVisual();
        var widget = new DesktopWidget();
        using var typography = WidgetTextRendering.Begin(graphics, widget);
        visual.Draw(graphics, "网络监控 Network", new(0, 0, 380, 120), Color.Black, 0);
        var original = visual.Height;
        widget.FontFamily = "Segoe UI";
        widget.FontScale = 1.5;
        widget.FontWeight = 700;
        widget.FontItalic = true;
        visual.Draw(graphics, "网络监控 Network", new(0, 0, 380, 120), Color.Black, 0);
        Assert.True(visual.Height > original);
    });

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { error = exception; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
