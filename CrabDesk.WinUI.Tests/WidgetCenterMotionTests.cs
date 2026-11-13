using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using CrabDesk.Core;
using CrabDesk.Runtime;
using WinRT;
using Xunit;
using Xunit.Abstractions;
using Forms = System.Windows.Forms;

namespace CrabDesk.WinUI.Tests;

[Collection("desktop-probe")]
public sealed class WidgetCenterMotionTests(ITestOutputHelper output)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void PanelCornersShowTheUnderlyingWindow() => OnSta(() =>
    {
        using var fixture = new WidgetCenterRuntimeTests.Fixture();
        Populate(fixture.Runtime);
        using var background = new Forms.Form { FormBorderStyle = Forms.FormBorderStyle.None,
            StartPosition = Forms.FormStartPosition.Manual, Bounds = new Rectangle(0, 0, 1200, 900),
            BackColor = Color.Magenta, TopMost = true, ShowInTaskbar = false };
        background.Show();
        background.BringToFront();
        background.Update();
        Pump(100);
        using var center = new WidgetCenterForm(fixture.Runtime, fixture.Runtime.Monitors[0]) { Owner = background };
        center.Present();
        Assert.True(SetWindowPos(background.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013));
        Assert.True(SetWindowPos(center.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013));
        Pump(400);
        using var image = new Bitmap(center.Width + 8, center.Height);
        using (var graphics = Graphics.FromImage(image))
            graphics.CopyFromScreen(new Point(center.Left - 4, center.Top), Point.Empty, image.Size);
        var evidence = Path.Combine(Path.GetTempPath(), "CrabDesk-WidgetCenter-Corners.png");
        image.Save(evidence);
        output.WriteLine($"Screen capture: {evidence}");
        foreach (var point in new[] { new Point(6, 2), new Point(6, image.Height - 3),
            new Point(image.Width - 7, 2), new Point(image.Width - 7, image.Height - 3) })
        {
            var pixel = image.GetPixel(point.X, point.Y);
            var outside = image.GetPixel(point.X == 6 ? 0 : image.Width - 1, point.Y);
            Assert.True(outside.R < 230 || outside.G < 230 || outside.B < 230, $"Background lacks contrast: {outside}");
            var difference = Math.Abs(pixel.R - outside.R) + Math.Abs(pixel.G - outside.G) + Math.Abs(pixel.B - outside.B);
            Assert.True(difference <= 30, $"Corner {point}: {pixel}; background={outside}");
        }
        Assert.True(image.GetPixel(24, 32).G > 50);
        center.Hide();
    });

    [Fact]
    public void PanelOwnsItsRoundedOutlineWithoutNativeWindowCorners() => OnSta(() =>
    {
        using var fixture = new WidgetCenterRuntimeTests.Fixture();
        Populate(fixture.Runtime);
        using var center = new WidgetCenterForm(fixture.Runtime, fixture.Runtime.Monitors[0]);
        center.Present();
        Pump(350);
        Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(center.Handle, 33, out var corners, sizeof(int)));
        Assert.Equal(1, corners);
        var frame = (Bitmap)Get(center, "_panelFrame")!;
        Assert.Equal(0, frame.GetPixel(0, frame.Height - 1).A);
        Assert.True(frame.GetPixel(16, frame.Height - 2).A > 0);
        center.RefreshTheme();
        Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(center.Handle, 33, out corners, sizeof(int)));
        Assert.Equal(1, corners);
        center.Hide();
    });

    [Fact]
    public void NativeFrameStaysDisabledThroughoutPanelMotion() => OnSta(() =>
    {
        using var fixture = new WidgetCenterRuntimeTests.Fixture();
        Populate(fixture.Runtime);
        using var center = new WidgetCenterForm(fixture.Runtime, fixture.Runtime.Monitors[0]);
        center.Present();
        Assert.Equal(0, NonClientRenderingEnabled(center));
        Pump(350);
        Assert.Equal(0, NonClientRenderingEnabled(center));
        center.RefreshTheme();
        Assert.Equal(0, NonClientRenderingEnabled(center));
        center.Dismiss();
        Assert.Equal(0, NonClientRenderingEnabled(center));
        Pump(350);
        Assert.False(center.Visible);
        center.Present();
        Assert.Equal(0, NonClientRenderingEnabled(center));
        Pump(350);
        Assert.Equal(0, NonClientRenderingEnabled(center));
        center.Hide();
    });

    private static int NonClientRenderingEnabled(Forms.Form form)
    {
        Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(form.Handle, 1, out var enabled, sizeof(int)));
        return enabled;
    }

    [Fact]
    public void CompositedDismissKeepsTheNativeWindowStationary() => OnSta(() =>
    {
        using var fixture = new WidgetCenterRuntimeTests.Fixture();
        Populate(fixture.Runtime);
        using var center = new WidgetCenterForm(fixture.Runtime, fixture.Runtime.Monitors[0]);
        center.Present();
        Pump(350);
        Assert.NotNull(Get(center, "_panelComposition"));
        var moves = 0;
        center.LocationChanged += (_, _) => moves++;
        using var probe = new PaintProbe(center);
        var started = Stopwatch.GetTimestamp();
        center.Dismiss();
        Pump(350);
        output.WriteLine($"Dismiss: moves={moves}, paints={probe.Paints}, paintMs={probe.PaintMilliseconds:F2}, elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");
        Assert.False(center.Visible);
        Assert.Equal(0, moves);
    });

    [Fact]
    public void ReopeningDuringDismissDoesNotResetTheSlide() => OnSta(() =>
    {
        using var fixture = new WidgetCenterRuntimeTests.Fixture();
        Populate(fixture.Runtime);
        using var center = new WidgetCenterForm(fixture.Runtime, fixture.Runtime.Monitors[0]);
        center.Present();
        Pump(350);
        center.Dismiss();
        Pump(70);
        var before = center.Location;
        var offset = center.PanelOffset;
        center.Present();
        Assert.InRange(Math.Abs(center.Left - before.X), 0, 2);
        Assert.InRange(Math.Abs((int)Get(center, "_panelFrom")! - offset), 0, 20);
        Pump(350);
        Assert.True(center.Visible);
        Assert.False(((DesktopAnimationFrameClock)Get(center, "_panelMotion")!).Enabled);
        center.Hide();
    });

    [Fact]
    public void ReducedMotionShowsAndHidesImmediately() => OnSta(() =>
    {
        using var fixture = new WidgetCenterRuntimeTests.Fixture();
        Populate(fixture.Runtime);
        fixture.Runtime.State.Settings.Appearance.AnimationEnabled = false;
        using var center = new WidgetCenterForm(fixture.Runtime, fixture.Runtime.Monitors[0]);
        center.Present();
        Assert.True(center.Visible);
        Assert.Equal(0, center.PanelOffset);
        Assert.False(((DesktopAnimationFrameClock)Get(center, "_panelMotion")!).Enabled);
        Assert.False((bool)Get(center, "_panelFrameFrozen")!);
        center.Dismiss();
        Assert.False(center.Visible);
        Assert.Equal(0, center.PanelOffset);
        Assert.False(((DesktopAnimationFrameClock)Get(center, "_panelMotion")!).Enabled);
    });

    [Fact]
    public void NativeFallbackReversesWithoutResettingAndRestoresItsPlacement() => OnSta(() =>
    {
        using var fixture = new WidgetCenterRuntimeTests.Fixture();
        Populate(fixture.Runtime);
        using var center = new WidgetCenterForm(fixture.Runtime, fixture.Runtime.Monitors[0]);
        center.Present();
        Pump(350);
        ((WidgetCenterComposition?)Get(center, "_panelComposition"))?.Dispose();
        typeof(WidgetCenterForm).GetField("_panelComposition", Hidden)!.SetValue(center, null);
        var origin = center.Location;
        center.Dismiss();
        Pump(50);
        var before = center.Location;
        Assert.True(before.X > origin.X);
        center.Present();
        Assert.Equal(before, center.Location);
        Pump(350);
        Assert.True(center.Visible);
        Assert.Equal(origin, center.Location);
        center.Dismiss();
        Pump(350);
        Assert.False(center.Visible);
        Assert.Equal(origin, center.Location);
    });

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void DismissingThePickerKeepsItsRowsInTheFrozenFrame(double scale) => OnSta(() =>
    {
        using var fixture = new WidgetCenterRuntimeTests.Fixture();
        Populate(fixture.Runtime);
        using var center = new WidgetCenterForm(fixture.Runtime, fixture.Runtime.Monitors[0]);
        center.Present();
        Pump(350);
        typeof(WidgetSurfaceController).GetField("_scale", Hidden)!.SetValue(center, scale);
        center.RefreshWidgets();
        center.AccessibilityObject.GetChild(0)!.DoDefaultAction();
        Pump(30);
        center.Dismiss();
        Assert.False((bool)Get(center, "_pickerVisible")!);
        var frame = (Bitmap)Get(center, "_panelFrame")!;
        var point = new Point((int)Math.Round(20 * scale), (int)Math.Round(100 * scale));
        var pixel = frame.GetPixel(point.X, point.Y);
        Assert.Equal(255, pixel.A);
        Assert.NotEqual(center.BackColor, pixel);
        center.Invalidate();
        center.Update();
        Assert.Equal(pixel, frame.GetPixel(point.X, point.Y));
        Pump(350);
        Assert.False(center.Visible);
    });

    private static void Populate(CrabDeskRuntime runtime)
    {
        runtime.State.Settings.WidgetCenter.Enabled = true;
        runtime.State.Settings.Appearance.AnimationEnabled = true;
        foreach (var kind in new[] { DesktopWidgetKind.Note, DesktopWidgetKind.Todo, DesktopWidgetKind.Clock,
            DesktopWidgetKind.Calendar, DesktopWidgetKind.Translation, DesktopWidgetKind.FileShelf })
        {
            var widget = new DesktopWidget
            {
                Kind = kind, Placement = WidgetPlacement.Center, CenterOrder = runtime.State.Widgets.Count,
                Text = "Animation rendering sample\nA second line of content", Translation = "Translation sample",
                Tasks = [new() { Text = "Check animation continuity" }]
            };
            runtime.State.Widgets.Add(widget);
        }
    }

    private static object? Get(object instance, string name) =>
        typeof(WidgetCenterForm).GetField(name, Hidden)!.GetValue(instance);

    private static void Pump(int milliseconds)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started).TotalMilliseconds < milliseconds)
        {
            Forms.Application.DoEvents();
            Thread.Sleep(1);
        }
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var previousDpiContext = SetThreadDpiAwarenessContext(new IntPtr(-4));
            global::Windows.System.DispatcherQueueController? queue = null;
            try
            {
                if (global::Windows.System.DispatcherQueue.GetForCurrentThread() is null)
                {
                    Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(new(12, 2, 2), out var raw));
                    try { queue = MarshalInterface<global::Windows.System.DispatcherQueueController>.FromAbi(raw); }
                    finally { Marshal.Release(raw); }
                }
                action();
            }
            catch (Exception error) { failure = error; }
            finally
            {
                if (queue is not null) { _ = queue.ShutdownQueueAsync(); Pump(30); }
                if (previousDpiContext != IntPtr.Zero) SetThreadDpiAwarenessContext(previousDpiContext);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class PaintProbe : Forms.NativeWindow, IDisposable
    {
        internal int Paints;
        internal double PaintMilliseconds;
        internal PaintProbe(Forms.Form form) => AssignHandle(form.Handle);
        protected override void WndProc(ref Forms.Message message)
        {
            var paint = message.Msg == 0x000F;
            var started = Stopwatch.GetTimestamp();
            base.WndProc(ref message);
            if (paint) { Paints++; PaintMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
        }
        public void Dispose() => ReleaseHandle();
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct QueueOptions(int Size, int ThreadType, int ApartmentType);
    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(QueueOptions options, out IntPtr controller);
    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
