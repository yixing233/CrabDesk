using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using CrabDesk.Core;
using CrabDesk.Native;
using CrabDesk.Runtime;
using Xunit;
using Forms = System.Windows.Forms;

namespace CrabDesk.WinUI.Tests;

public sealed class CrossMonitorWidgetAndBoxDragTests
{
    [Theory]
    [InlineData(1, 1.25)]
    [InlineData(1.25, 1)]
    [InlineData(2, 1.5)]
    public void AcrylicTargetFrameKeepsBoxContentsDuringTheCrossing(double sourceScale, double targetScale) => OnSta(() =>
    {
        using var fixture = new Fixture(sourceScale, targetScale);
        var box = new DesktopBox { MonitorId = fixture.Source.Id, Title = "box", Bounds = new(60, 60, 300, 200) };
        box.Appearance.Background = "#FF202020";
        box.Appearance.ShowItemLabels = true;
        fixture.Runtime.State.Boxes.Add(box);
        var item = new DesktopItemRef { Key = new("path", "cross-monitor-test.txt"), ParsingName = "cross-monitor-test.txt", DisplayName = "visible contents", Kind = DesktopItemKind.File };
        fixture.Runtime.State.Assignments[item.Key.ToString()] = box.Id;
        Set(fixture.Runtime, "<Items>k__BackingField", new[] { item });
        using var source = new DesktopBoxForm(fixture.Runtime, fixture.Source);
        using var target = new DesktopBoxForm(fixture.Runtime, fixture.Target);
        source.SetAcrylicBackground(true); target.SetAcrylicBackground(true);
        Call(source, "RebuildBoxItemCache"); Call(target, "RebuildBoxItemCache");
        source.ConfigureCrossMonitorDragging([source, target], _ => { });
        target.ConfigureCrossMonitorDragging([source, target], _ => { });
        Set(source, "_movingBox", box);
        box.MonitorId = fixture.Target.Id;
        box.Bounds = new(150, 100, 300, 200);
        // A desktop refresh during capture must retain the departing box's data.
        Call(source, "RebuildBoxItemCache");
        Call(source, "RebuildGeometry"); Call(target, "RebuildGeometry");
        using var bitmap = new Bitmap((int)(800 * targetScale), (int)(600 * targetScale));
        Call(target, "PaintAcrylicFrame", bitmap);
        var contentPixels = 0;
        for (var y = (int)(145 * targetScale); y < (int)(260 * targetScale); y++)
            for (var x = (int)(165 * targetScale); x < (int)(430 * targetScale); x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.A > 150 && pixel.R > 170 && pixel.G > 170 && pixel.B > 170) contentPixels++;
            }
        Assert.True(contentPixels > 30, $"Target card lost its contents while dragging: {contentPixels} light pixels.");
        Set(source, "_movingBox", null);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AcrylicDragPublishesTheDestinationAndClearsItWhenReturning(bool effectsEnabled) => OnSta(() =>
    {
        using var fixture = new Fixture(1.25, 1);
        var box = new DesktopBox { MonitorId = fixture.Source.Id, Bounds = new(60, 60, 300, 200), ExpandOnHover = true };
        fixture.Runtime.State.Boxes.Add(box);
        using var source = new DesktopBoxForm(fixture.Runtime, fixture.Source);
        using var target = new DesktopBoxForm(fixture.Runtime, fixture.Target);
        var manager = (DesktopSurfaceManager)RuntimeHelpers.GetUninitializedObject(typeof(DesktopSurfaceManager));
        Set(manager, "_surfaces", new List<DesktopBoxForm> { source, target });
        var frames = new List<bool>();
        var backgrounds = new List<AcrylicBoxRegion>();
        foreach (var form in new[] { source, target })
        {
            form.Location = new(-12000, -12000); _ = form.Handle;
            Set(form, "_desktopAttached", true); Set(form, "_hitMaskPresented", true);
            form.SetAcrylicBackground(effectsEnabled);
            form.ConfigureCrossMonitorDragging([source, target], s => Call(manager, "RequestAcrylicDragFrames", s));
            form.SetAcrylicFramePresenter((bitmap, _, regions) =>
            {
                backgrounds.AddRange(regions.Where(region => region.Id == box.Id));
                if (ReferenceEquals(form, target)) frames.Add(bitmap.GetPixel(200, 160).A > 0);
            });
        }
        ((HashSet<Guid>)typeof(DesktopBoxForm).GetField("_hoverExpandedBoxes", Hidden)!.GetValue(source)!).Add(box.Id);
        Set(source, "_movingBox", box);
        box.MonitorId = fixture.Target.Id; box.Bounds = new(150, 100, 300, 200);
        source.MarkMonitorTransferRefreshPending(fixture.Source.Id);
        Call(source, "RenderPendingDragFrame");
        Assert.Equal(new[] { true }, frames);
        Assert.Equal(2, backgrounds.Count);
        Assert.Equal(backgrounds[0], backgrounds[1]);
        Assert.Equal(200, backgrounds[1].ScreenBounds.Height);
        backgrounds.Clear();
        box.Bounds = box.Bounds with { X = 170 };
        Call(source, "RenderPendingDragFrame");
        Assert.Equal(new[] { true, true }, frames);
        Assert.Equal(2, backgrounds.Count);
        Assert.Equal(backgrounds[0], backgrounds[1]);
        box.MonitorId = fixture.Source.Id; box.Bounds = new(60, 60, 300, 200);
        source.MarkMonitorTransferRefreshPending(fixture.Target.Id);
        Call(source, "RenderPendingDragFrame");
        Assert.Equal(new[] { true, true, false }, frames);
        Set(source, "_movingBox", null);
    });

    [Theory]
    [InlineData(1, 1.25, true)]
    [InlineData(1.25, 1, true)]
    [InlineData(2, 1.5, true)]
    [InlineData(1, 1.25, false)]
    [InlineData(1.25, 1, false)]
    [InlineData(2, 1.5, false)]
    public void BothFramesKeepTheSameBackdropDuringRepeatedMoves(double sourceScale, double targetScale, bool expanded) => OnSta(() =>
    {
        using var fixture = new Fixture(sourceScale, targetScale);
        var box = new DesktopBox { MonitorId = fixture.Source.Id, Bounds = new(60, 60, 300, 200), ExpandOnHover = true };
        fixture.Runtime.State.Boxes.Add(box);
        using var source = new DesktopBoxForm(fixture.Runtime, fixture.Source);
        using var target = new DesktopBoxForm(fixture.Runtime, fixture.Target);
        source.ConfigureCrossMonitorDragging([source, target], _ => { });
        target.ConfigureCrossMonitorDragging([source, target], _ => { });
        var expandedForm = expanded ? source : target;
        ((HashSet<Guid>)typeof(DesktopBoxForm).GetField("_hoverExpandedBoxes", Hidden)!.GetValue(expandedForm)!).Add(box.Id);
        Set(source, "_movingBox", box);
        box.MonitorId = fixture.Target.Id;
        for (var step = 0; step < 10; step++)
        {
            box.Bounds = new(150 + step * 10, 100 + step * 3, 300, 200);
            Call(source, "RebuildGeometry"); Call(target, "RebuildGeometry");
            var sourceRegion = Assert.Single(source.GetAcrylicRegions());
            var targetRegion = Assert.Single(target.GetAcrylicRegions());
            Assert.Equal(sourceRegion, targetRegion);
            Assert.Equal((expanded ? 200 : box.Appearance.TitleBarHeight) * targetScale, targetRegion.ScreenBounds.Height, 3);
        }
        Set(source, "_movingBox", null);
    });

    [Fact]
    public void CompositorKeepsTheExpandedBackdropWhileBothMonitorFramesArePublished() => OnSta(() =>
    {
        if (!DesktopAcrylicHost.IsSupported) return;
        using var fixture = new Fixture(1.25, 1);
        var box = new DesktopBox { MonitorId = fixture.Source.Id, Bounds = new(60, 60, 300, 200), ExpandOnHover = true };
        fixture.Runtime.State.Boxes.Add(box);
        using var source = new DesktopBoxForm(fixture.Runtime, fixture.Source);
        using var target = new DesktopBoxForm(fixture.Runtime, fixture.Target);
        source.ConfigureCrossMonitorDragging([source, target], _ => { });
        target.ConfigureCrossMonitorDragging([source, target], _ => { });
        ((HashSet<Guid>)typeof(DesktopBoxForm).GetField("_hoverExpandedBoxes", Hidden)!.GetValue(source)!).Add(box.Id);
        Set(source, "_movingBox", box);
        box.MonitorId = fixture.Target.Id;
        using var desktop = new Forms.Form();
        using var host = new DesktopAcrylicHost(desktop.Handle, [fixture.Source, fixture.Target]);
        host.InitializeComposition();
        using var bitmap = new Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var backgrounds = (System.Collections.IDictionary)typeof(DesktopAcrylicHost).GetField("_boxes", Hidden)!.GetValue(host)!;
        object? firstBackground = null;
        for (var step = 0; step < 10; step++)
        {
            box.Bounds = new(150 + step * 10, 100 + step * 3, 300, 200);
            Call(source, "RebuildGeometry"); Call(target, "RebuildGeometry");
            foreach (var form in new[] { source, target })
            {
                host.PresentForeground(form.MonitorId, bitmap, Point.Empty, form.GetAcrylicRegions().ToArray());
                Assert.Single(backgrounds.Keys.Cast<Guid>());
                var background = backgrounds[box.Id]!;
                firstBackground ??= background;
                Assert.Same(firstBackground, background);
                var visual = (global::Windows.UI.Composition.SpriteVisual)background.GetType().GetField("Visual", Hidden)!.GetValue(background)!;
                Assert.Equal(200f, visual.Size.Y);
                Assert.Equal(300f, visual.Size.X);
            }
        }
        Set(source, "_movingBox", null);
    });

    [Theory]
    [InlineData(1, 1.25)]
    [InlineData(1.25, 1)]
    [InlineData(2, 1.5)]
    public void WidgetTracksBothScreensBeforeReleaseAndSavesOnce(double sourceScale, double targetScale) => OnSta(() =>
    {
        using var fixture = new Fixture(sourceScale, targetScale);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Note, Size = DesktopWidgetSize.Custom,
            MonitorId = fixture.Source.Id, Bounds = new(60, 60, 300, 200), Text = "keep this text" };
        fixture.Runtime.State.Widgets.Add(widget);
        using var form = new DesktopBoxForm(fixture.Runtime, fixture.Source);
        var changes = 0; fixture.Runtime.Changed += (_, _) => changes++;
        var press = new PointF(100, 72);
        Call(form, "HandleWidgetMouseDown", Mouse(press, sourceScale), press);
        var targetPoint = SourcePoint(fixture, 250, 150);
        Call(form, "HandleWidgetMouseMove", targetPoint);
        Assert.Equal(fixture.Target.Id, widget.MonitorId);
        Assert.Equal(250 - 40 * sourceScale / targetScale, widget.Bounds.X, 3);
        Assert.Equal(150 - 12 * sourceScale / targetScale, widget.Bounds.Y, 3);
        Assert.Equal((300d, 200d), (widget.Bounds.Width, widget.Bounds.Height));
        Assert.Equal(0, changes);
        Call(form, "HandleWidgetMouseMove", new PointF(420, 250));
        Assert.Equal(fixture.Source.Id, widget.MonitorId);
        Assert.Equal(380, widget.Bounds.X, 3);
        Call(form, "HandleWidgetMouseMove", targetPoint);
        var lastBounds = widget.Bounds;
        Call(form, "HandleWidgetMouseUp", Mouse(targetPoint, sourceScale), targetPoint);
        Assert.Equal(fixture.Target.Id, widget.MonitorId);
        Assert.Equal(lastBounds, widget.Bounds);
        Assert.Equal("keep this text", widget.Text);
        Assert.Equal(1, changes);
    });

    private static PointF SourcePoint(Fixture fixture, double targetX, double targetY) => new(
        (float)((fixture.Target.PixelBounds.X + targetX * fixture.Target.DpiScale - fixture.Source.PixelBounds.X) / fixture.Source.DpiScale),
        (float)((fixture.Target.PixelBounds.Y + targetY * fixture.Target.DpiScale - fixture.Source.PixelBounds.Y) / fixture.Source.DpiScale));
    private static Forms.MouseEventArgs Mouse(PointF point, double scale) => new(Forms.MouseButtons.Left, 1,
        (int)Math.Round(point.X * scale), (int)Math.Round(point.Y * scale), 0);

    private sealed class Fixture : IDisposable
    {
        public CrabDeskRuntime Runtime { get; } = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
        public MonitorLayout Source { get; }
        public MonitorLayout Target { get; }
        private readonly RuntimeTimer _timer = new(TimeSpan.FromHours(1), false, _ => { }, () => { });
        public Fixture(double sourceScale, double targetScale)
        {
            Source = Monitor("source", sourceScale, 0, 0, true);
            Target = Monitor("target", targetScale, -800 * targetScale, 80, false);
            Set(Runtime, "<State>k__BackingField", new CrabDeskState());
            Set(Runtime, "<Items>k__BackingField", Array.Empty<DesktopItemRef>());
            Set(Runtime, "<Monitors>k__BackingField", new[] { Source, Target });
            Set(Runtime, "_iconProvider", new ShellIconProvider());
            Set(Runtime, "_saveTimer", _timer);
        }
        public void Dispose() => _timer.Dispose();
        private static MonitorLayout Monitor(string id, double scale, double x, double y, bool primary) => new()
        {
            Id = id, DeviceName = id, DpiScale = scale, IsPrimary = primary,
            Bounds = new(x / scale, y / scale, 800, 600), WorkArea = new(x / scale, y / scale, 800, 570),
            PixelBounds = new(x, y, 800 * scale, 600 * scale), PixelWorkArea = new(x, y, 800 * scale, 570 * scale)
        };
    }
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Hidden)!.SetValue(target, value);
    private static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Hidden)!.Invoke(target, args);
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { failure = exception; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
