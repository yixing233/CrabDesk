using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using CrabDesk.Core;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopLyricsRenderingTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlaybackUsesTheAnimationCadenceAndStopsWhenPaused(bool scrolling) => InSta(() =>
    {
        var (runtime, monitor, lyrics, _) = Fixture(1.25);
        lyrics.Lyrics = lyrics.Lyrics! with { Media = lyrics.Lyrics.Media! with
        {
            IsPlaying = true, CapturedAt = DateTimeOffset.Now
        } };
        if (scrolling)
        {
            runtime.State.Settings.Appearance.AnimationEnabled = true;
            lyrics.Bounds = lyrics.Bounds with { Height = 444 };
            lyrics.LyricsLineSpacing = 1.45;
            var text = lyrics.Lyrics.Document!.Lines[0].Text;
            lyrics.Lyrics = lyrics.Lyrics with { Document = new(Enumerable.Range(0, 30)
                .Select(index => new WidgetLyricLine(text, index * 1000, (index + 1) * 1000,
                    [new(text, index * 1000, (index + 1) * 1000)])).ToArray(), "Vertical motion") };
        }
        using var desktop = new System.Windows.Forms.Form
        {
            ShowInTaskbar = false, StartPosition = System.Windows.Forms.FormStartPosition.Manual,
            Location = new Point(-32000, -32000)
        };
        desktop.Show();
        using var form = new DesktopBoxForm(runtime, monitor);
        form.AttachToDesktop(desktop.Handle);
        form.SetAcrylicBackground(true);
        form.SetAcrylicFramePresenter((_, _, _) => { });
        var frames = 0;
        Call(form, "SetAcrylicPartialFramePresenter", (Action<Bitmap, Rectangle>)((_, _) => frames++));
        Assert.True(form.RefreshWorkspace(), form.LayerDiagnostic);
        form.Show();
        Call(form, "UpdateLyricsAnimationTimer");
        var timer = (System.Windows.Forms.Timer)typeof(DesktopBoxForm).GetField("_lyricsAnimationTimer", Hidden)!.GetValue(form)!;
        Assert.NotNull(timer);
        Assert.True(timer.Enabled);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var ticks = new List<double>();
        timer.Tick += (_, _) => ticks.Add(watch.Elapsed.TotalMilliseconds);
        // A posted deadline exits even when animation keeps the message queue busy.
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var frame = new System.Windows.Threading.DispatcherFrame();
        using var deadline = new System.Threading.Timer(_ => dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Send, (Action)(() => frame.Continue = false)),
            null, 1500, Timeout.Infinite);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        watch.Stop();
        Assert.True(frames > 0);
        var intervals = ticks.Zip(ticks.Skip(1), (first, second) => second - first).OrderBy(value => value).ToArray();
        var output = Environment.GetEnvironmentVariable("CRABDESK_LYRICS_VISUAL_OUTPUT") ?? Path.GetTempPath();
        Directory.CreateDirectory(output);
        var label = Environment.GetEnvironmentVariable("CRABDESK_PERF_LABEL") ?? "current";
        File.WriteAllText(Path.Combine(output, $"lyrics-{(scrolling ? "scrolling" : "playback")}-cadence-{label}.json"),
            System.Text.Json.JsonSerializer.Serialize(new { Frames = frames,
                FramesPerSecond = frames / watch.Elapsed.TotalSeconds,
                MedianIntervalMilliseconds = intervals.Length == 0 ? 0 : intervals[intervals.Length / 2] }));

        lyrics.Lyrics = lyrics.Lyrics with { Media = lyrics.Lyrics.Media! with { IsPlaying = false } };
        using var paused = DesktopLayerBitmapFactory.Create(form.ClientSize.Width, form.ClientSize.Height);
        Call(form, "PaintAcrylicFrame", paused);
        Assert.False(timer.Enabled);
        Assert.Equal(DesktopAnimationFrameClock.IntervalMilliseconds, timer.Interval);
    });

    [Theory]
    [InlineData(1d, false)]
    [InlineData(1.25d, false)]
    [InlineData(1d, true)]
    [InlineData(1.25d, true)]
    public void PartialLyricsMatchFullPaintingAndReuseTheResidentFrame(double scale, bool transparent) => InSta(() =>
    {
        var (runtime, monitor, lyrics, note) = Fixture(scale);
        lyrics.LyricsTransparentBackground = transparent;
        if (transparent)
        {
            lyrics.LyricsFontFamily = "Consolas";
            lyrics.LyricsFontWeight = 700;
            lyrics.LyricsFontStyle = WidgetLyricsFontStyle.Italic;
        }
        runtime.State.Boxes.Add(new DesktopBox { MonitorId = monitor.Id, Bounds = new(20, 286, 372, 180),
            ExpandOnHover = true, IsCollapsed = true, Appearance = new BoxAppearance { TitleBarHeight = 32 } });
        using var form = new DesktopBoxForm(runtime, monitor);
        using var desktop = new System.Windows.Forms.Form();
        form.AttachToDesktop(desktop.Handle);
        form.SetAcrylicBackground(true);
        Bitmap? resident = null;
        Bitmap? partialFrame = null;
        Rectangle dirty = default;
        form.SetAcrylicFramePresenter((bitmap, _, _) => resident = bitmap);
        Call(form, "SetAcrylicPartialFramePresenter", (Action<Bitmap, Rectangle>)((bitmap, region) =>
        {
            Assert.Same(resident, bitmap);
            dirty = region;
            partialFrame?.Dispose();
            partialFrame = (Bitmap)bitmap.Clone();
        }));
        Assert.True(form.RefreshWorkspace(), form.LayerDiagnostic);
        Assert.NotNull(resident);
        using var before = (Bitmap)resident!.Clone();
        lyrics.Lyrics = lyrics.Lyrics! with { Media = lyrics.Lyrics.Media! with { Position = TimeSpan.FromSeconds(7) } };
        Assert.True((bool)Call(form, "TryPresentLyricsFrame")!);
        Assert.NotNull(partialFrame);
        try
        {
            Assert.True(dirty.Width * dirty.Height < resident.Width * resident.Height / 3);
            using var expected = DesktopLayerBitmapFactory.Create(resident.Width, resident.Height);
            Call(form, "PaintAcrylicFrame", expected);
            Assert.Equal(Pixels(expected), Pixels(partialFrame!));
            var notePixels = Rectangle.Round(new RectangleF((float)(note.Bounds.X * scale), (float)(note.Bounds.Y * scale),
                (float)(note.Bounds.Width * scale), (float)(note.Bounds.Height * scale)));
            using var oldNote = before.Clone(notePixels, PixelFormat.Format32bppPArgb);
            using var newNote = partialFrame!.Clone(notePixels, PixelFormat.Format32bppPArgb);
            Assert.Equal(Pixels(oldNote), Pixels(newNote));
            Assert.False(Pixels(before).SequenceEqual(Pixels(partialFrame)));
            Assert.True((bool)Call(form, "TryPresentLyricsFrame")!);
        }
        finally { partialFrame?.Dispose(); }
    });

    [Theory]
    [InlineData("overlap")]
    [InlineData("lyrics-overlap")]
    [InlineData("drag")]
    [InlineData("resize")]
    public void PartialLyricsFallBackForOverlapAndActiveTransforms(string reason) => InSta(() =>
    {
        var (runtime, monitor, lyrics, note) = Fixture(1);
        using var form = new DesktopBoxForm(runtime, monitor);
        using var desktop = new System.Windows.Forms.Form();
        form.AttachToDesktop(desktop.Handle);
        form.SetAcrylicBackground(true);
        form.SetAcrylicFramePresenter((_, _, _) => { });
        Call(form, "SetAcrylicPartialFramePresenter", (Action<Bitmap, Rectangle>)((_, _) => Assert.Fail("Must preserve full overlap/transform painting.")));
        Assert.True(form.RefreshWorkspace(), form.LayerDiagnostic);
        if (reason is "overlap" or "lyrics-overlap")
        {
            note.Bounds = lyrics.Bounds with { X = lyrics.Bounds.X + 100 };
            if (reason == "lyrics-overlap") { note.Kind = DesktopWidgetKind.Lyrics; note.Lyrics = lyrics.Lyrics; }
        }
        else Set(form, reason == "drag" ? "_widgetDragging" : "_widgetResizing", true);
        Assert.False((bool)Call(form, "TryPresentLyricsFrame")!);
    });

    [Fact]
    public void AlternatingForegroundBuffersIncludeThePreviousDirtyPixels()
    {
        var method = typeof(DesktopAcrylicHost).GetMethod("CalculateForegroundUploadBounds", BindingFlags.Static | BindingFlags.NonPublic)!;
        var size = new Size(800, 600);
        var first = new Rectangle(100, 200, 80, 50);
        var next = new Rectangle(200, 220, 100, 80);
        Assert.Equal(Rectangle.Union(first, next), (Rectangle)method.Invoke(null, [size, next, first, true])!);
        Assert.Equal(new Rectangle(Point.Empty, size), (Rectangle)method.Invoke(null, [size, next, first, false])!);
        Assert.Equal(next, (Rectangle)method.Invoke(null, [size, next, Rectangle.Empty, true])!);
    }

    [Fact]
    public void AcrylicPartialUploadsReuseResourcesAcrossFramesAndResize() => InSta(() =>
    {
        if (!DesktopAcrylicHost.IsSupported) return;
        var (_, monitor, _, _) = Fixture(1);
        using var desktop = new System.Windows.Forms.Form();
        using var host = new DesktopAcrylicHost(desktop.Handle, [monitor]);
        host.InitializeComposition();
        using var bitmap = new Bitmap(800, 600, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.DarkBlue);
        host.PresentForeground("test", bitmap, Point.Empty, []);
        for (var index = 0; index < 6; index++)
        {
            var dirty = new Rectangle(20 + index * 40, 40, 40, 40);
            using var brush = new SolidBrush(index % 2 == 0 ? Color.Red : Color.Green);
            graphics.FillRectangle(brush, dirty);
            Call(host, "PresentPartialForeground", "test", bitmap, dirty);
        }
        using var resized = new Bitmap(900, 650, PixelFormat.Format32bppPArgb);
        host.PresentForeground("test", resized, Point.Empty, []);
        Call(host, "PresentPartialForeground", "test", resized, new Rectangle(10, 10, 50, 50));
    });

    private static (CrabDeskRuntime, MonitorLayout, DesktopWidget, DesktopWidget) Fixture(double scale)
    {
        var runtime = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
        var monitor = new MonitorLayout { Id = "test", DeviceName = "test", IsPrimary = true, DpiScale = scale,
            Bounds = new(0, 0, 1000, 700), WorkArea = new(0, 0, 1000, 700),
            PixelBounds = new(0, 0, 1000 * scale, 700 * scale), PixelWorkArea = new(0, 0, 1000 * scale, 700 * scale) };
        var state = new CrabDeskState();
        state.Settings.Appearance.AnimationEnabled = false;
        Set(runtime, "<State>k__BackingField", state);
        Set(runtime, "<Items>k__BackingField", Array.Empty<DesktopItemRef>());
        Set(runtime, "<Monitors>k__BackingField", new[] { monitor });
        Set(runtime, "_beginInvoke", (Action<Action>)(_ => { }));
        var text = "清晨的微光穿过窗边落在街道尽头，风带着云朵向前走";
        var lyrics = new DesktopWidget { Kind = DesktopWidgetKind.Lyrics, MonitorId = monitor.Id, Size = DesktopWidgetSize.Panel,
            Bounds = new(20, 330, 372, 292), Lyrics = new(new ControlCenterMedia("Song", "Artist", "", false, false, false, false)
                { Position = TimeSpan.FromSeconds(2), Duration = TimeSpan.FromSeconds(10) },
                new([new(text, 0, 10000, [new(text, 0, 10000)])], "Fixture")) };
        var note = new DesktopWidget { Kind = DesktopWidgetKind.Note, MonitorId = monitor.Id, Bounds = new(500, 20, 180, 180), Text = "静态内容" };
        state.Widgets.Add(lyrics); state.Widgets.Add(note);
        return (runtime, monitor, lyrics, note);
    }

    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Hidden)!.SetValue(target, value);
    private static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Hidden)!.Invoke(target, args);
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
