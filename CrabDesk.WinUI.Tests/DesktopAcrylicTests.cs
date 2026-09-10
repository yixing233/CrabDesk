using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CrabDesk.Core;
using CrabDesk.Native;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopAcrylicTests
{
    [Fact]
    public void AcrylicHostRestoresExternalHideWhileDesktopDisplayIsRequested()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var desktop = new System.Windows.Forms.Form();
                desktop.Show();
                using var host = new DesktopAcrylicHost(desktop.Handle, [Monitor(0, 0, 1)]);
                host.ShowAtDesktop();
                ShowWindow(host.Handle, 0);
                Assert.False(IsWindowVisible(host.Handle));
                var timer = (System.Windows.Forms.Timer)typeof(DesktopAcrylicHost)
                    .GetField("_orderTimer", Flags)!.GetValue(host)!;
                typeof(System.Windows.Forms.Timer).GetMethod("OnTick", Flags)!
                    .Invoke(timer, [EventArgs.Empty]);
                Assert.True(IsWindowVisible(host.Handle), "Win+D must not permanently hide desktop boxes.");
                host.HideFromDesktop();
                typeof(System.Windows.Forms.Timer).GetMethod("OnTick", Flags)!
                    .Invoke(timer, [EventArgs.Empty]);
                Assert.False(IsWindowVisible(host.Handle), "Application-requested hiding must remain effective.");
            }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DesktopPlacementShowsHiddenHostAndItsChildren(bool alreadyOrdered)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var desktop = new System.Windows.Forms.Form();
                using var host = new System.Windows.Forms.Form();
                using var child = new System.Windows.Forms.Panel();
                host.Controls.Add(child);
                desktop.Show();
                _ = host.Handle;
                _ = child.Handle;
                if (alreadyOrdered)
                    Assert.True(DesktopAcrylicWindowTools.PlaceAtDesktop(host.Handle, desktop.Handle));
                // Reproduce STARTUPINFO/SW_HIDE leaving the host hidden even
                // though its children have their own visible style set.
                ShowWindow(host.Handle, 0);
                Assert.False(IsWindowVisible(host.Handle));

                Assert.True(DesktopAcrylicWindowTools.PlaceAtDesktop(host.Handle, desktop.Handle));

                Assert.True(IsWindowVisible(host.Handle), "The acrylic host remained hidden.");
                Assert.True(IsWindowVisible(child.Handle), "The box child is hidden by its host.");
            }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [Fact]
    public void BoxHandleCreationDefersPresentationUntilDesktopAttachment()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var runtime = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
                var monitor = Monitor(0, 0, 1);
                var state = new CrabDeskState();
                state.Boxes.Add(new DesktopBox { MonitorId = monitor.Id, Bounds = new(30, 50, 230, 300) });
                Set(runtime, "<State>k__BackingField", state);
                Set(runtime, "<Items>k__BackingField", Array.Empty<DesktopItemRef>());
                Set(runtime, "<Monitors>k__BackingField", new[] { monitor });
                using var form = new DesktopBoxForm(runtime, monitor);
                _ = form.Handle;
                form.Invalidate();
                Assert.False(form.IsLayerReady);
                using var parent = new System.Windows.Forms.Form();
                form.AttachToDesktop(parent.Handle);
                Assert.True(form.RefreshWorkspace(), form.LayerDiagnostic);
                Assert.True(form.IsLayerReady);
                Assert.True(form.ValidateWindowRegion());
            }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Fact]
    public void AcrylicSeparatesDesktopIconsFromSharpBoxForeground()
    {
        var source = File.ReadAllText(Path.Combine(
            FindSolutionDirectory(),
            "CrabDesk.Runtime",
            "DesktopSurfaceManager.cs"));
        var constructorStart = source.IndexOf(
            "internal DesktopSurfaceManager(",
            StringComparison.Ordinal);
        var constructorEnd = source.IndexOf(
            "// Refreshes every surface after a workspace change",
            Math.Max(0, constructorStart),
            StringComparison.Ordinal);
        Assert.True(constructorStart >= 0);
        Assert.True(constructorEnd > constructorStart);
        var constructor = source[constructorStart..constructorEnd];

        Assert.Contains(
            "DesktopWindowTools.AttachAsDesktopChild(iconSurface.Handle, iconParentHandle);",
            constructor,
            StringComparison.Ordinal);
        Assert.Contains(
            "surface.AttachToDesktop(boxParentHandle);",
            constructor,
            StringComparison.Ordinal);
        var acrylicFallback = constructor.IndexOf(
            "if (_acrylicHost is null)",
            StringComparison.Ordinal);
        var sharedComposition = constructor.IndexOf(
            "ConfigureBoxIconLayerComposition();",
            Math.Max(0, acrylicFallback),
            StringComparison.Ordinal);
        Assert.True(acrylicFallback >= 0);
        Assert.True(sharedComposition > acrylicFallback);
        Assert.Contains(
            "surface.SetAcrylicFramePresenter((bitmap, origin, regions) =>",
            constructor,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingSettingsKeepTheirMaterialAndAcrylicChoiceRoundTrips()
    {
        Assert.False(JsonSerializer.Deserialize<GlobalAppearanceSettings>("{}")!.UseAcrylicBoxes);
        var settings = new GlobalAppearanceSettings { UseAcrylicBoxes = true };
        Assert.True(JsonSerializer.Deserialize<GlobalAppearanceSettings>(JsonSerializer.Serialize(settings))!.UseAcrylicBoxes);
    }

    [Theory]
    [InlineData(0.35)]
    [InlineData(1.0)]
    public void AcrylicTintKeepsTheLiveBackgroundVisible(double opacity)
    {
        var tint = DesktopBoxForm.ResolveBoxTintOpacity(opacity, true);
        Assert.InRange(tint, 0.01, 0.99);
        Assert.Equal(opacity, DesktopBoxForm.ResolveBoxTintOpacity(opacity, false));
    }

    [Fact]
    public void BackdropUsesScreenPixelsIncludingNegativeMonitorOriginsAndDpi()
    {
        var monitor = Monitor(-1920, 120, 1.5);
        var result = DesktopBoxForm.CreateAcrylicRegion(Guid.NewGuid(), new RectangleF(20, 40, 200, 38), monitor, 8);
        Assert.Equal(new RectangleF(-1890, 180, 300, 57), result.ScreenBounds);
        Assert.Equal(12, result.CornerRadius);
    }

    [Fact]
    public void CompletedCollapsedBoxRemainsInBackdropWhileOtherBoxesExpand()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var runtime = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
                var monitor = Monitor(0, 0, 1);
                var state = new CrabDeskState();
                Set(runtime, "<State>k__BackingField", state);
                Set(runtime, "<Items>k__BackingField", Array.Empty<DesktopItemRef>());
                Set(runtime, "<Monitors>k__BackingField", new[] { monitor });
                var first = new DesktopBox { MonitorId = monitor.Id, ExpandOnHover = true, Bounds = new(30, 50, 230, 300) };
                var second = new DesktopBox { MonitorId = monitor.Id, ExpandOnHover = true, Bounds = new(300, 50, 230, 300) };
                state.Boxes.AddRange([first, second]);
                using var form = new DesktopBoxForm(runtime, monitor);
                var expanded = (HashSet<Guid>)typeof(DesktopBoxForm).GetField("_hoverExpandedBoxes", Flags)!.GetValue(form)!;
                expanded.Add(first.Id);
                expanded.Add(second.Id);
                Assert.All(form.GetAcrylicRegions(), region => Assert.Equal(300, region.ScreenBounds.Height));
                expanded.Remove(first.Id);
                Set(form, "_geometryDirty", true);
                var regions = form.GetAcrylicRegions().ToDictionary(region => region.Id);
                Assert.Equal(first.Appearance.TitleBarHeight, regions[first.Id].ScreenBounds.Height);
                Assert.Equal(300, regions[second.Id].ScreenBounds.Height);
                state.Boxes.Remove(first);
                Set(form, "_geometryDirty", true);
                Assert.Equal(second.Id, Assert.Single(form.GetAcrylicRegions()).Id);
            }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Acrylic geometry test did not finish.");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void AcrylicFramesKeepForegroundPixelsAndBackdropTogether(double scale)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var runtime = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
                var monitor = Monitor(-1920, 120, scale);
                var state = new CrabDeskState();
                var box = new DesktopBox { MonitorId = monitor.Id, Bounds = new(30, 50, 230, 300) };
                state.Boxes.Add(box);
                Set(runtime, "<State>k__BackingField", state);
                Set(runtime, "<Items>k__BackingField", Array.Empty<DesktopItemRef>());
                Set(runtime, "<Monitors>k__BackingField", new[] { monitor });
                using var form = new DesktopBoxForm(runtime, monitor);
                using var parent = new System.Windows.Forms.Form();
                form.AttachToDesktop(parent.Handle);
                form.SetAcrylicBackground(true);
                Assert.True(form.RefreshWorkspace(), form.LayerDiagnostic);

                var frameCount = 0;
                form.SetAcrylicFramePresenter((bitmap, origin, regions) =>
                {
                    var bounds = box.Bounds;
                    var height = box.ExpandOnHover ? box.Appearance.TitleBarHeight : bounds.Height;
                    var region = Assert.Single(regions);
                    Assert.Equal(new RectangleF(
                        (float)(monitor.PixelBounds.X + bounds.X * scale),
                        (float)(monitor.PixelBounds.Y + bounds.Y * scale),
                        (float)(bounds.Width * scale), (float)(height * scale)), region.ScreenBounds);
                    Assert.Equal(form.PointToScreen(Point.Empty), origin);
                    var y = (int)((bounds.Y + 18) * scale);
                    Assert.True(bitmap.GetPixel((int)((bounds.X + 16) * scale), y).A > 0,
                        "The sharp foreground must be present at this frame's backdrop position.");
                    Assert.Equal(0, bitmap.GetPixel((int)((bounds.X - 12) * scale), y).A);
                    Assert.Equal(0, bitmap.GetPixel((int)((bounds.X + bounds.Width + 12) * scale), y).A);
                    if (box.ExpandOnHover)
                        Assert.Equal(0, bitmap.GetPixel((int)((bounds.X + 16) * scale),
                            (int)((bounds.Y + 100) * scale)).A);
                    if (!box.ExpandOnHover)
                    {
                        // Empty acrylic interiors should contain only the uniform
                        // translucent tint, including moving and resizing frames.
                        var left = (int)((bounds.X + 40) * scale);
                        var top = (int)((bounds.Y + 100) * scale);
                        var tint = bitmap.GetPixel(left, top);
                        Assert.InRange((int)tint.A, 1, 254);
                        for (var dy = 0; dy < 20; dy++)
                            for (var dx = 0; dx < 20; dx++)
                                Assert.Equal(tint, bitmap.GetPixel(left + dx, top + dy));
                    }
                    frameCount++;
                });
                var present = typeof(DesktopBoxForm).GetMethod("PresentLayer", Flags)!;
                Set(form, "_movingBox", box);
                Set(form, "_startBounds", box.Bounds);
                foreach (var position in new[] { new Point(70, 90), new Point(390, 130), new Point(140, 80), new Point(30, 50) })
                {
                    box.Bounds = box.Bounds with { X = position.X, Y = position.Y };
                    Assert.True((bool)present.Invoke(form, null)!, form.LayerDiagnostic);
                }
                Set(form, "_movingBox", null!);
                Set(form, "_resizingBox", box);
                box.Bounds = box.Bounds with { Width = 360, Height = 400 };
                Assert.True(form.UpdateInteractionRegion(), form.LayerDiagnostic);
                Set(form, "_resizingBox", null!);
                box.ExpandOnHover = true;
                Assert.True(form.RefreshWorkspace(), form.LayerDiagnostic);
                Assert.Equal(6, frameCount);
                Assert.True(form.ValidateWindowRegion());
            }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Acrylic frame test did not finish.");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Fact]
    public void HidingExplorerIconsDoesNotHideAcrylicBoxes()
    {
        var visible = DesktopSurfaceManager.ResolveVisibility(
            appVisible: true,
            desktopIconsVisible: false);

        Assert.False(visible.ShowIcons);
        Assert.True(visible.ShowBoxes);

        var appHidden = DesktopSurfaceManager.ResolveVisibility(
            appVisible: false,
            desktopIconsVisible: true);
        Assert.False(appHidden.ShowIcons);
        Assert.False(appHidden.ShowBoxes);
    }

    [Fact]
    public void LatestMonitorFrameOwnsBackdropDuringHandoff()
    {
        if (!DesktopAcrylicHost.IsSupported) return;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var desktop = new System.Windows.Forms.Form();
                using var host = new DesktopAcrylicHost(desktop.Handle, [Monitor(0, 0, 1)]);
                host.InitializeComposition();
                using var bitmap = new Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                var id = Guid.NewGuid();
                var first = new AcrylicBoxRegion(id, new RectangleF(30, 50, 64, 64), 8);
                var second = first with { ScreenBounds = new RectangleF(300, 50, 64, 64) };
                host.PresentForeground("first", bitmap, Point.Empty, [first]);
                host.PresentForeground("second", bitmap, Point.Empty, [second]);
                var boxes = (System.Collections.IDictionary)typeof(DesktopAcrylicHost).GetField("_boxes", Flags)!.GetValue(host)!;
                var box = boxes[id]!;
                var visual = (global::Windows.UI.Composition.SpriteVisual)box.GetType().GetField("Visual", Flags)!.GetValue(box)!;
                Assert.Equal(300f, visual.Offset.X);
                host.PresentForeground("first", bitmap, Point.Empty, [first]);
                Assert.Equal(30f, visual.Offset.X);
                host.PresentForeground("second", bitmap, Point.Empty, []);
                Assert.Equal(30f, visual.Offset.X);
                host.PresentForeground("first", bitmap, Point.Empty, []);
                Assert.Empty(boxes);
            }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Monitor handoff test did not finish.");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Fact]
    public void OverlappingBoxBlursLowerTitleButKeepsItsOwnHeaderSharp()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var runtime = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
                var monitor = Monitor(0, 0, 1);
                var state = new CrabDeskState();
                state.Boxes.AddRange([
                    new DesktopBox { Title = "LOWER BLUR THIS TEXT", MonitorId = monitor.Id, Bounds = new(30, 120, 320, 280) },
                    new DesktopBox { Title = "UPPER KEEP SHARP", MonitorId = monitor.Id, Bounds = new(170, 60, 320, 280) }
                ]);
                Set(runtime, "<State>k__BackingField", state);
                Set(runtime, "<Items>k__BackingField", Array.Empty<DesktopItemRef>());
                Set(runtime, "<Monitors>k__BackingField", new[] { monitor });
                using var form = new DesktopBoxForm(runtime, monitor);
                using var parent = new System.Windows.Forms.Form();
                form.AttachToDesktop(parent.Handle);
                form.SetAcrylicBackground(true);
                Bitmap? presented = null;
                try
                {
                    form.SetAcrylicFramePresenter((bitmap, _, _) =>
                    {
                        presented?.Dispose();
                        presented = (Bitmap)bitmap.Clone();
                    });
                    Assert.True(form.RefreshWorkspace(), form.LayerDiagnostic);
                    Assert.NotNull(presented);
                    using var reference = DesktopLayerBitmapFactory.Create(presented!.Width, presented.Height);
                    using (var graphics = Graphics.FromImage(reference))
                        typeof(DesktopBoxForm).GetMethod("PaintRegularSurface", Flags)!.Invoke(form, [graphics]);

                    // Compare to the original sharp painter with the SAME tint,
                    // text, geometry and input state, not a screenshot of another app.
                    var coveredTitle = new Rectangle(178, 127, 80, 25);
                    Assert.True(EdgeEnergy(presented, coveredTitle) < EdgeEnergy(reference, coveredTitle) * 0.5,
                        "The lower title must lose its sharp edges under the upper box.");
                    for (var y = 66; y < 96; y++)
                        for (var x = 190; x < 460; x++)
                            Assert.Equal(reference.GetPixel(x, y), presented.GetPixel(x, y));
                }
                finally { presented?.Dispose(); }
            }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static long EdgeEnergy(Bitmap bitmap, Rectangle region)
    {
        long energy = 0;
        for (var y = region.Top; y < region.Bottom; y++)
            for (var x = region.Left + 1; x < region.Right; x++)
            {
                var a = bitmap.GetPixel(x - 1, y);
                var b = bitmap.GetPixel(x, y);
                energy += Math.Abs(a.A - b.A) + Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
            }
        return energy;
    }
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(object instance, string name, object value) => instance.GetType().GetField(name, Flags)!.SetValue(instance, value);
    private static MonitorLayout Monitor(double x, double y, double scale) => new()
    {
        Id = "acrylic-test", DeviceName = "acrylic-test", DpiScale = scale,
        Bounds = new(0, 0, 1280, 720), WorkArea = new(0, 0, 1280, 720),
        PixelBounds = new(x, y, 1280 * scale, 720 * scale), PixelWorkArea = new(x, y, 1280 * scale, 720 * scale)
    };

    private static string FindSolutionDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CrabDesk.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the CrabDesk solution directory.");
    }
}
