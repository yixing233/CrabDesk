using System.Collections;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using CrabDesk.Core;
using CrabDesk.Native;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopSurfaceMemoryTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void EmptyMonitorReleasesItsForegroundAndAllocatesAgainWhenContentReturns() => InSta(() =>
    {
        var (runtime, monitor) = Fixture();
        using var desktop = new System.Windows.Forms.Form();
        using var form = new DesktopBoxForm(runtime, monitor);
        form.AttachToDesktop(desktop.Handle);
        form.SetAcrylicBackground(true);
        var fullFrames = 0;
        var emptyFrames = 0;
        Bitmap? previous = null;
        form.SetAcrylicFramePresenter((bitmap, _, _) => { fullFrames++; previous = bitmap; });
        Call(form, "SetAcrylicEmptyFramePresenter", (Action)(() => emptyFrames++));

        Assert.True(form.RefreshWorkspace(), form.LayerDiagnostic);
        Assert.Equal(0, fullFrames);
        Assert.Equal(1, emptyFrames);
        Assert.Null(Get(form, "_acrylicFrameBitmap"));

        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Note, MonitorId = monitor.Id,
            Bounds = new(20, 20, 180, 180), Text = "Memory fixture" };
        runtime.State.Widgets.Add(widget);
        Assert.True(form.RefreshWorkspace(), form.LayerDiagnostic);
        Assert.NotNull(previous);
        var first = previous;
        AssertHitMaskReleased(form);

        runtime.State.Widgets.Clear();
        Assert.True(form.RefreshWorkspace(), form.LayerDiagnostic);
        Assert.Equal(2, emptyFrames);
        Assert.Null(Get(form, "_acrylicFrameBitmap"));
        Assert.ThrowsAny<Exception>(() => first!.GetPixel(0, 0));

        runtime.State.Widgets.Add(widget);
        Assert.True(form.RefreshWorkspace(), form.LayerDiagnostic);
        Assert.Equal(2, fullFrames);
        Assert.NotSame(first, previous);
        AssertHitMaskReleased(form);
    });

    [Fact]
    public void ForegroundCleanupDisposesUploadsAndPreservesOtherMonitors() => InSta(() =>
    {
        if (!DesktopAcrylicHost.IsSupported) return;
        var (_, monitor) = Fixture();
        using var desktop = new System.Windows.Forms.Form();
        using var host = new DesktopAcrylicHost(desktop.Handle, [monitor]);
        host.InitializeComposition();
        using var bitmap = new Bitmap(400, 300, PixelFormat.Format32bppPArgb);
        var firstRegion = new AcrylicBoxRegion(Guid.NewGuid(), new(20, 20, 80, 80), 8);
        var secondRegion = new AcrylicBoxRegion(Guid.NewGuid(), new(200, 20, 80, 80), 8);
        host.PresentForeground("first", bitmap, Point.Empty, [firstRegion]);
        host.PresentForeground("second", bitmap, Point.Empty, [secondRegion]);
        host.PresentPartialForeground("first", bitmap, new(20, 20, 40, 40));
        var frames = (IDictionary)Get(host, "_foregrounds")!;
        var removed = frames["first"]!;
        var survivor = frames["second"];
        var upload = (CompositionBitmapDevice.UploadBitmap)Get(removed, "_uploadBitmap")!;
        Assert.NotEqual(IntPtr.Zero, upload.BitmapPointer);

        Call(host, "ClearForeground", "first");
        Assert.Single(frames.Keys.Cast<object>());
        Assert.Same(survivor, frames["second"]);
        Assert.Equal(IntPtr.Zero, upload.BitmapPointer);
        Assert.Equal(IntPtr.Zero, upload.RectanglePointer);
        var boxes = (IDictionary)Get(host, "_boxes")!;
        Assert.False(boxes.Contains(firstRegion.Id));
        Assert.True(boxes.Contains(secondRegion.Id));
        Call(host, "ClearForeground", "first");
        Assert.Single(frames.Keys.Cast<object>());

        host.PresentForeground("first", bitmap, Point.Empty, [firstRegion]);
        Assert.Equal(2, frames.Count);
        Assert.NotSame(removed, frames["first"]);
        host.PresentPartialForeground("first", bitmap, new(20, 20, 40, 40));
    });

    [Fact]
    public void InputOnlySurfaceReleasesUploadAndRetriesAfterResize() => InSta(() =>
    {
        var (runtime, monitor) = Fixture();
        runtime.State.Boxes.Add(new DesktopBox { MonitorId = monitor.Id, Bounds = new(20, 20, 220, 180) });
        using var desktop = new System.Windows.Forms.Form();
        using var form = new DesktopBoxForm(runtime, monitor);
        form.PrepareIconLayerComposition();
        form.AttachToDesktop(desktop.Handle);
        Assert.True(form.RefreshWorkspace(), form.LayerDiagnostic);
        AssertHitMaskReleased(form);
        form.ClientSize = new Size(950, 650);
        Assert.True(form.RefreshWorkspace(), form.LayerDiagnostic);
        AssertHitMaskReleased(form);
        form.GetType().GetField("_hitMaskPresented", Hidden)!.SetValue(form, false);
        Assert.True(form.RefreshWorkspace(), form.LayerDiagnostic);
        AssertHitMaskReleased(form);
    });

    [Fact]
    public void UploadedInputPixelsRemainHitTestableWithoutTheUploadBuffers() => InSta(() =>
    {
        var (runtime, monitor) = Fixture();
        runtime.State.Boxes.Add(new DesktopBox { MonitorId = monitor.Id, Bounds = new(20, 20, 220, 180) });
        using var parent = new System.Windows.Forms.Form { FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
            StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new(20, 20), ClientSize = new(400, 260) };
        using var form = new DesktopBoxForm(runtime, monitor);
        form.PrepareIconLayerComposition();
        form.AttachToDesktop(parent.Handle);
        Assert.True(form.RefreshWorkspace(), form.LayerDiagnostic);
        AssertHitMaskReleased(form);
        Assert.True(form.ValidateWindowRegion());
        // Check actual alpha hit testing without synthesizing input events.
        Assert.True(SetWindowPos(parent.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0010 | 0x0001 | 0x0002 | 0x0040));
        ShowWindow(form.Handle, 4);
        Assert.Equal(form.Handle, WindowFromPoint(form.PointToScreen(new Point(60, 35))));
        Assert.NotEqual(form.Handle, WindowFromPoint(form.PointToScreen(new Point(350, 230))));
    });

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    private static void AssertHitMaskReleased(DesktopBoxForm form)
    {
        Assert.True((bool)Get(form, "_hitMaskPresented")!);
        Assert.Equal(form.ClientSize, Get(form, "_hitMaskSize"));
        var surfaces = (IDictionary)typeof(LayeredWindowPresenter).GetField("Surfaces", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Assert.False(surfaces.Contains(form.Handle), "A one-shot input upload must not retain its monitor-sized DIB.");
    }

    private static (CrabDeskRuntime, MonitorLayout) Fixture()
    {
        var runtime = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
        var monitor = new MonitorLayout { Id = "memory-test", DeviceName = "memory-test", IsPrimary = true, DpiScale = 1,
            Bounds = new(0, 0, 1000, 700), WorkArea = new(0, 0, 1000, 700),
            PixelBounds = new(0, 0, 1000, 700), PixelWorkArea = new(0, 0, 1000, 700) };
        var state = new CrabDeskState();
        state.Settings.Appearance.AnimationEnabled = false;
        Set(runtime, "<State>k__BackingField", state);
        Set(runtime, "<Items>k__BackingField", Array.Empty<DesktopItemRef>());
        Set(runtime, "<Monitors>k__BackingField", new[] { monitor });
        Set(runtime, "_beginInvoke", (Action<Action>)(_ => { }));
        return (runtime, monitor);
    }

    private static object? Get(object target, string name)
    {
        var field = target.GetType().GetField(name, Hidden);
        Assert.NotNull(field);
        return field.GetValue(target);
    }
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Hidden)!.SetValue(target, value);
    private static object? Call(object target, string name, params object[] args)
    {
        var method = target.GetType().GetMethod(name, Hidden);
        Assert.NotNull(method);
        return method.Invoke(target, args);
    }
    private static void InSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception caught) { error = caught; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
