using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using CrabDesk.Core;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

/// <summary>
/// "显示外边框" shipped as a setting with no renderer behind it: the toggle
/// persisted but nothing drew a stroke. These render a real box through the
/// production painter and read the pixels back, so the setting cannot regress
/// into a no-op again.
/// </summary>
public sealed class BoxBorderRenderingTests
{
    [Fact]
    public void EnablingTheBorderPaintsARimAlongTheBoxEdge()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var dark = PaintBox(showBorder: false);
                var bordered = PaintBox(showBorder: true);

                // The box spans x=100..300, so its left edge sits at x=100.
                // Probe there, midway down: on the stroke, but clear of the
                // rounded corners and the header.
                const int edgeX = 100;
                const int interiorX = 160;
                const int probeY = 200;
                Assert.NotEqual(
                    dark.GetPixel(edgeX, probeY),
                    bordered.GetPixel(edgeX, probeY));

                // The rim must read as a lighter edge on this dark box.
                var rim = bordered.GetPixel(edgeX, probeY);
                var fill = bordered.GetPixel(interiorX, probeY);
                Assert.True(Luminance(rim) > Luminance(fill),
                    "The border must be brighter than the fill it surrounds on a dark box.");

                // The interior stays untouched: this is an outline, not a tint.
                Assert.Equal(dark.GetPixel(interiorX, probeY), bordered.GetPixel(interiorX, probeY));
                Assert.Equal(dark.GetPixel(200, 250), bordered.GetPixel(200, 250));

                dark.Dispose();
                bordered.Dispose();
            }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Border render test did not finish.");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Fact]
    public void DisablingTheBorderLeavesNoBrighterEdgeAlongTheBox()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var bitmap = PaintBox(showBorder: false);
                var fill = bitmap.GetPixel(160, 200);

                // Without the border no pixel along the box edge may be
                // brighter than the fill. Antialiasing on the rounded outline
                // only ever blends toward the transparent outside, so a
                // brighter pixel here would mean a rim was still drawn.
                for (var x = 100; x <= 106; x++)
                {
                    Assert.True(Luminance(bitmap.GetPixel(x, 200)) <= Luminance(fill),
                        $"Pixel x={x} is brighter than the fill, so a rim was painted.");
                }
            }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Border render test did not finish.");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Fact]
    public void BorderStaysInsideTheBoxBoundsSoNeighboursDoNotOverlap()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var bitmap = PaintBox(showBorder: true);

                // Everything strictly outside the box rectangle must stay
                // transparent, so two adjacent boxes cannot paint over each
                // other's rim.
                for (var y = 100; y < 400; y++)
                {
                    Assert.Equal(0, bitmap.GetPixel(99, y).A);
                    Assert.Equal(0, bitmap.GetPixel(301, y).A);
                }
            }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Border render test did not finish.");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Fact]
    public void WiderBorderPaintsMoreEdgePixels()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var reference = PaintBox(showBorder: false);
                using var thin = PaintBox(showBorder: true, width: 1, color: "#FFFF0000");
                using var thick = PaintBox(showBorder: true, width: 6, color: "#FFFF0000");

                var thinCount = CountRimPixels(reference, thin);
                var thickCount = CountRimPixels(reference, thick);

                Assert.True(thickCount > thinCount,
                    $"A 6 px border must cover more pixels than a 1 px one (thin={thinCount}, thick={thickCount}).");
            }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Border render test did not finish.");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Fact]
    public void ConfiguredBorderColorIsTheColorActuallyPainted()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var bitmap = PaintBox(showBorder: true, width: 4, color: "#FFFF0000");

                // Sample the middle of the left edge, well inside the stroke.
                var pixel = bitmap.GetPixel(101, 200);
                Assert.True(pixel.R > 200 && pixel.G < 60 && pixel.B < 60,
                    $"The configured red border must be painted red, got {pixel}.");
            }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Border render test did not finish.");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Fact]
    public void ZeroBorderOpacityPaintsNothingEvenWhenTheBorderIsOn()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var reference = PaintBox(showBorder: false);
                using var invisible = PaintBox(showBorder: true, width: 4, opacity: 0);

                // A fully transparent stroke must leave the edge pixel-identical
                // to the same box drawn with the border switched off.
                for (var y = 120; y < 280; y++)
                {
                    for (var x = 100; x <= 106; x++)
                    {
                        Assert.Equal(reference.GetPixel(x, y), invisible.GetPixel(x, y));
                    }
                }
            }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Border render test did not finish.");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>
    /// Counts pixels along the left edge that the border changed relative to
    /// the same box rendered with the border off. Comparing against that
    /// reference rather than the fill keeps the rounded corner's own
    /// antialiasing out of the count.
    /// </summary>
    private static int CountRimPixels(Bitmap reference, Bitmap bordered)
    {
        var count = 0;
        for (var x = 100; x <= 112; x++)
        {
            if (reference.GetPixel(x, 200) != bordered.GetPixel(x, 200))
            {
                count++;
            }
        }
        return count;
    }

    private static Bitmap PaintBox(
        bool showBorder,
        double width = 1,
        string color = "Auto",
        double opacity = 100)
    {
        var runtime = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
        var monitor = Monitor();
        var state = new CrabDeskState();
        state.Settings.Appearance.ShowBorder = showBorder;
        state.Settings.Appearance.BorderWidth = width;
        state.Settings.Appearance.BorderColor = color;
        state.Settings.Appearance.BorderOpacity = opacity;
        state.Boxes.Add(new DesktopBox
        {
            Title = "盒子",
            MonitorId = monitor.Id,
            Bounds = new LayoutRect(100, 100, 200, 200)
        });
        Set(runtime, "<State>k__BackingField", state);
        Set(runtime, "<Items>k__BackingField", Array.Empty<DesktopItemRef>());
        Set(runtime, "<Monitors>k__BackingField", new[] { monitor });
        using var form = new DesktopBoxForm(runtime, monitor);
        using var parent = new System.Windows.Forms.Form();
        form.AttachToDesktop(parent.Handle);

        var bitmap = DesktopLayerBitmapFactory.Create(400, 400);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        typeof(DesktopBoxForm).GetMethod("PaintRegularSurface", Flags)!.Invoke(form, [graphics]);
        return bitmap;
    }

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    private static void Set(object instance, string name, object value) =>
        instance.GetType().GetField(name, Flags)!.SetValue(instance, value);

    private static MonitorLayout Monitor() => new()
    {
        Id = "border-test",
        DeviceName = "border-test",
        DpiScale = 1,
        Bounds = new(0, 0, 400, 400),
        WorkArea = new(0, 0, 400, 400),
        PixelBounds = new(0, 0, 400, 400),
        PixelWorkArea = new(0, 0, 400, 400)
    };

    private static double Luminance(Color color) =>
        0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B;
}
