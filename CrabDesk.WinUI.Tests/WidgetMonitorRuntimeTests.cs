using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using System.Runtime.CompilerServices;
using CrabDesk.Core;
using CrabDesk.Runtime;
using CrabDesk.Native;
using Moq;
using System.Collections.Concurrent;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class WidgetMonitorRuntimeTests
{
    [Fact]
    public async Task MediaDragPreviewsWithoutWritingAndSeeksOnceWhenReleasedOutsideTheCard()
    {
        var runtime = Fixture();
        var media = new ControlCenterMedia("Track", "Artist", "Album", true, true, true, true)
        { TrackId = "track", CanSeek = true, Duration = TimeSpan.FromSeconds(100), Position = TimeSpan.FromSeconds(20), CapturedAt = DateTimeOffset.Now };
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.ControlCenter, MonitorId = "test", Size = DesktopWidgetSize.Panel,
            Bounds = new(100, 100, 372, 292), Controls = new() { Media = media } };
        runtime.State.Widgets.Add(widget);
        var requested = new TaskCompletionSource<(ControlCenterMedia Media, TimeSpan Position)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Mock<IWidgetControlCenterMonitor>();
        source.Setup(s => s.SeekMediaAsync(It.IsAny<ControlCenterMedia>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns((ControlCenterMedia track, TimeSpan position, CancellationToken _) =>
            { requested.TrySetResult((track, position)); return Task.FromResult(true); });
        source.Setup(s => s.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(widget.Controls);
        Set(runtime, "_controlMonitor", source.Object);
        OnSta(() =>
        {
            using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { }); Set(runtime, "_saveTimer", timer);
            using var form = new DesktopBoxForm(runtime, runtime.Monitors[0]);
            var body = ControlCenterPresentation.Body(372, 292, runtime.State.Settings.Appearance.CornerRadius);
            var track = ControlCenterPresentation.MediaProgress(ControlCenterPresentation.MediaPanel(ControlCenterPresentation.MediaArea(body)));
            var start = new PointF((float)(100 + track.X + track.Width / 4), (float)(100 + track.Y + 2));
            typeof(DesktopBoxForm).GetMethod("BeginControlCenterPress", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [widget, start]);
            Set(form, "_pressedWidget", widget);
            var move = typeof(DesktopBoxForm).GetMethod("UpdateControlCenterDrag", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (var fraction = .3; fraction < 1; fraction += .1)
                Assert.True((bool)move.Invoke(form, [widget, new PointF((float)(100 + track.X + track.Width * fraction), start.Y)])!);
            source.Verify(s => s.SeekMediaAsync(It.IsAny<ControlCenterMedia>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
            var end = new PointF(700, start.Y);
            Assert.True((bool)typeof(DesktopBoxForm).GetMethod("HandleWidgetMouseUp", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(form, [new MouseEventArgs(MouseButtons.Left, 1, 700, (int)end.Y, 0), end])!);
            Assert.Equal(new LayoutRect(100, 100, 372, 292), widget.Bounds);
        });
        var command = await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(media, command.Media);
        Assert.Equal(TimeSpan.FromSeconds(100), command.Position);
        source.Verify(s => s.SeekMediaAsync(media, TimeSpan.FromSeconds(100), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("changed")] [InlineData("unsupported")] [InlineData("removed")] [InlineData("cancelled")]
    public void MediaDragCannotSeekAnUnavailableOrChangedTrack(string situation) => OnSta(() =>
    {
        var runtime = Fixture();
        var media = new ControlCenterMedia("Track", "", "", true, true, true, true)
        { TrackId = "first", CanSeek = situation != "unsupported", Duration = TimeSpan.FromSeconds(100) };
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.ControlCenter, MonitorId = "test", Size = DesktopWidgetSize.Panel,
            Bounds = new(100, 100, 372, 292), Controls = new() { Media = media } };
        runtime.State.Widgets.Add(widget);
        var source = new Mock<IWidgetControlCenterMonitor>(); Set(runtime, "_controlMonitor", source.Object);
        using var form = new DesktopBoxForm(runtime, runtime.Monitors[0]);
        var body = ControlCenterPresentation.Body(372, 292, runtime.State.Settings.Appearance.CornerRadius);
        var track = ControlCenterPresentation.MediaProgress(ControlCenterPresentation.MediaPanel(ControlCenterPresentation.MediaArea(body)));
        var point = new PointF((float)(100 + track.X + track.Width / 2), (float)(100 + track.Y + 2));
        typeof(DesktopBoxForm).GetMethod("BeginControlCenterPress", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [widget, point]);
        if (situation == "changed") widget.Controls = widget.Controls with { Media = media with { TrackId = "second" } };
        if (situation == "removed") runtime.State.Widgets.Remove(widget);
        if (situation == "cancelled") typeof(DesktopBoxForm).GetMethod("EndControlCenterPress", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
        typeof(DesktopBoxForm).GetMethod("CompleteControlSeek", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [widget, point]);
        source.Verify(s => s.SeekMediaAsync(It.IsAny<ControlCenterMedia>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MediaArtworkRendersAndChangesWithoutRetainingThePreviousCover(bool dark) => OnSta(() =>
    {
        var runtime = Fixture();
        runtime.State.Settings.DefaultBoxAppearance.Background = dark ? "#24272D" : "#F8F9FB";
        var media = new ControlCenterMedia("专辑中的一首歌", "歌手 · 测试", "Album", true, true, true, true)
        { TrackId = "art", CanSeek = true, Duration = TimeSpan.FromSeconds(210), Position = TimeSpan.FromSeconds(65), CapturedAt = DateTimeOffset.Now };
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.ControlCenter, MonitorId = "test", Size = DesktopWidgetSize.Panel,
            Bounds = new(20, 20, 372, 292), Controls = new()
            { Media = media, VolumePercent = 42, VolumeMuted = false, BrightnessPercent = 65, WiFiEnabled = true, BluetoothEnabled = true, OutputDeviceName = "耳机 · 音频设备" } };
        runtime.State.Widgets.Add(widget);
        using var form = new DesktopBoxForm(runtime, runtime.Monitors[0]);
        var body = ControlCenterPresentation.Body(372, 292, runtime.State.Settings.Appearance.CornerRadius);
        var artwork = ControlCenterPresentation.MediaArtwork(ControlCenterPresentation.MediaPanel(ControlCenterPresentation.MediaArea(body)));
        var center = new Point((int)(20 + artwork.X + artwork.Width / 2), (int)(20 + artwork.Y + artwork.Height / 2));
        using var bitmap = new Bitmap(412, 332);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        foreach (var color in new[] { Color.Coral, Color.RoyalBlue })
        {
            using var cover = new Bitmap(48, 48);
            using (var paint = Graphics.FromImage(cover)) paint.Clear(color);
            using var stream = new MemoryStream(); cover.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            widget.Controls = widget.Controls with { Media = media with { Artwork = stream.ToArray() } };
            graphics.Clear(dark ? Color.FromArgb(28, 30, 36) : Color.White);
            typeof(DesktopBoxForm).GetMethod("DrawWidget", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [graphics, widget]);
            Assert.Equal(color.ToArgb(), bitmap.GetPixel(center.X, center.Y).ToArgb());
        }
        bitmap.Save(Path.Combine(AppContext.BaseDirectory, dark ? "control-center-media-dark.png" : "control-center-media-light.png"));
        widget.Controls = widget.Controls with { Media = media with { Artwork = [0, 1, 2] } };
        typeof(DesktopBoxForm).GetMethod("DrawWidget", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [graphics, widget]);
        Assert.NotEqual(Color.RoyalBlue.ToArgb(), bitmap.GetPixel(center.X, center.Y).ToArgb());
    });

    [Theory]
    [InlineData("#111827", null, true)]
    [InlineData("#F5F5F5", null, false)]
    [InlineData("#111827", false, false)]
    [InlineData("#F5F5F5", true, true)]
    public void InkMenuShowsTheCurrentShadeAndSavesTheSelection(string background, bool? initial, bool lightText) => OnSta(() =>
    {
        var runtime = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        runtime.State.Settings.DefaultBoxAppearance.Background = background;
        var widget = new DesktopWidget
        {
            Kind = DesktopWidgetKind.ControlCenter, MonitorId = "test", Size = DesktopWidgetSize.Panel,
            Bounds = new(20, 20, 372, 292), ControlCenterLightText = initial
        };
        runtime.State.Widgets.Add(widget);
        var changes = 0;
        runtime.Changed += (_, _) => changes++;
        using var form = new DesktopBoxForm(runtime, runtime.Monitors[0]);
        using var menu = form.BuildControlCenterInkMenu(widget);
        var choices = menu.DropDownItems.OfType<ToolStripMenuItem>().ToArray();
        Assert.Equal("文字和图标颜色", menu.Text);
        Assert.Equal(new[] { "浅色", "深色" }, choices.Select(choice => choice.Text));
        Assert.Equal(lightText, choices[0].Checked);
        Assert.Equal(!lightText, choices[1].Checked);

        choices[1].PerformClick();
        Assert.False(widget.ControlCenterLightText);
        Assert.False(choices[0].Checked);
        Assert.True(choices[1].Checked);
        choices[0].PerformClick();
        Assert.True(widget.ControlCenterLightText);
        Assert.True(choices[0].Checked);
        Assert.False(choices[1].Checked);
        Assert.Equal(2, changes);
        Assert.Equal(background, runtime.State.Settings.DefaultBoxAppearance.Background);
    });

    [Theory]
    [InlineData("#111827", "#4EA1D3", true)]
    [InlineData("#111827", "#FBBF24", true)]
    [InlineData("#F5F5F5", "#111827", false)]
    [InlineData("#808080", "#4EA1D3", false)]
    public void InkChoiceChangesLabelsAndOperationsWhileKeepingArtworkAndPanels(string background, string accent, bool automaticLight) => OnSta(() =>
    {
        var runtime = Fixture();
        runtime.State.Settings.DefaultBoxAppearance.Background = background;
        runtime.State.Settings.DefaultBoxAppearance.Accent = accent;
        var widget = new DesktopWidget
        {
            Kind = DesktopWidgetKind.ControlCenter, MonitorId = "test", Size = DesktopWidgetSize.Panel,
            Bounds = new(20, 20, 372, 292),
            Controls = new()
            {
                WiFiEnabled = true, BluetoothEnabled = false, VolumePercent = 42, VolumeMuted = false,
                OutputDeviceName = "Speakers", BrightnessPercent = 60,
                Media = new("Track", "Artist", "", false, true, true, true)
            }
        };
        runtime.State.Widgets.Add(widget);
        using var form = new DesktopBoxForm(runtime, runtime.Monitors[0]);
        var draw = typeof(DesktopBoxForm).GetMethod("DrawWidget", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Bitmap Render(bool? light)
        {
            widget.ControlCenterLightText = light;
            var bitmap = new Bitmap(412, 332);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Gray);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            draw.Invoke(form, [graphics, widget]);
            return bitmap;
        }
        using var light = Render(true);
        using var dark = Render(false);
        using var automatic = Render(null);
        var body = ControlCenterPresentation.Body(372, 292, runtime.State.Settings.Appearance.CornerRadius);
        var sliders = ControlCenterPresentation.SliderArea(body);
        var media = ControlCenterPresentation.MediaPanel(ControlCenterPresentation.MediaArea(body));
        var tile = ControlCenterPresentation.Toggle(body, 0);
        var offTile = ControlCenterPresentation.Toggle(body, 1);
        var regions = new[]
        {
            new LayoutRect(tile.X + (tile.Width - 18) / 2, tile.Y + 5, 18, 18),
            new LayoutRect(offTile.X + (offTile.Width - 18) / 2, offTile.Y + 5, 18, 18),
            new LayoutRect(sliders.X + 26, sliders.Y, 48, 16),
            ControlCenterPresentation.VolumeMuteButton(body),
            new LayoutRect(media.X + 10, media.Y + 8, 100, 18),
            ControlCenterPresentation.MediaButtonRects(media)[1]
        };
        foreach (var region in new[] { regions[2], regions[3], regions[4], regions[5],
            new LayoutRect(tile.X, tile.Y + 27, tile.Width, 28),
            new LayoutRect(offTile.X, offTile.Y + 27, offTile.Width, 28) })
            Assert.True(AverageInk(light, region) > AverageInk(dark, region) + 2, $"Ink did not change in {region}");
        foreach (var region in new[] { regions[0], regions[1] })
            Assert.Equal(AverageInk(light, region), AverageInk(dark, region), 6);
        var configured = automaticLight ? light : dark;
        foreach (var region in new[]
        {
            regions[0], regions[1], regions[^1],
            new LayoutRect(tile.X, tile.Y + 27, tile.Width, 28),
            new LayoutRect(offTile.X, offTile.Y + 27, offTile.Width, 28),
            new LayoutRect(tile.X + tile.Width - 15, tile.Y + 8, 10, 12),
            new LayoutRect(offTile.X + offTile.Width - 15, offTile.Y + 8, 10, 12)
        })
            Assert.Equal(AverageInk(configured, region), AverageInk(automatic, region), 6);
        foreach (var point in new[] { new Point((int)offTile.X + 4, (int)offTile.Y + 15), new Point((int)media.X + 4, (int)media.Y + 15) })
            Assert.Equal(light.GetPixel(point.X + 20, point.Y + 20), dark.GetPixel(point.X + 20, point.Y + 20));

        static double AverageInk(Bitmap bitmap, LayoutRect region)
        {
            double sum = 0; var count = 0;
            for (var y = (int)region.Y; y < region.Y + region.Height; y++)
                for (var x = (int)region.X; x < region.X + region.Width; x++)
                {
                    var color = bitmap.GetPixel(x + 20, y + 20);
                    sum += (color.R + color.G + color.B) / 3d; count++;
                }
            return sum / count;
        }
    });

    [Fact]
    public void ControlCenterOnlySurfacePollsAndRepaintsWithoutOtherMonitorWidgets() => OnSta(() =>
    {
        var runtime = Fixture(); Set(runtime, "<Items>k__BackingField", Array.Empty<DesktopItemRef>());
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.ControlCenter, MonitorId = "test", Size = DesktopWidgetSize.Panel, Bounds = new(20, 20, 372, 292) };
        runtime.State.Widgets.Add(widget);
        var source = new Mock<IWidgetControlCenterMonitor>();
        source.Setup(s => s.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ControlCenterSnapshot { VolumePercent = 42, BrightnessPercent = 61, WiFiEnabled = true });
        Set(runtime, "_controlMonitor", source.Object);
        using var form = new DesktopBoxForm(runtime, runtime.Monitors[0]);
        form.Location = new(-12000, -12000); _ = form.Handle;
        var renders = 0;
        Set(form, "_isCompositedByIconSurface", true);
        Set(form, "_iconLayerRenderRequest", (Action)(() => renders++));
        var manager = (DesktopSurfaceManager)RuntimeHelpers.GetUninitializedObject(typeof(DesktopSurfaceManager));
        Set(manager, "_surfaces", new List<DesktopBoxForm> { form }); Set(runtime, "_surfaceManager", manager);
        form.Show();
        var framesBeforeSampling = renders;
        var timer = (System.Windows.Forms.Timer)typeof(DesktopBoxForm).GetField("_widgetTimer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
        typeof(System.Windows.Forms.Timer).GetMethod("OnTick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(timer, [EventArgs.Empty]);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (widget.Controls is null && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
        Assert.NotNull(widget.Controls); Assert.Equal(42, widget.Controls!.VolumePercent); Assert.Equal(61, widget.Controls.BrightnessPercent);
        Assert.True(renders > framesBeforeSampling);
        source.Verify(s => s.ReadAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        form.Hide();
    });

    [Fact]
    public async Task AddingControlCenterStartsItsFirstSample()
    {
        var runtime = Fixture(); using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { }); Set(runtime, "_saveTimer", timer);
        var manager = (DesktopSurfaceManager)RuntimeHelpers.GetUninitializedObject(typeof(DesktopSurfaceManager));
        Set(manager, "_surfaces", new List<DesktopBoxForm>()); Set(runtime, "_surfaceManager", manager);
        var source = new Mock<IWidgetControlCenterMonitor>();
        source.Setup(s => s.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ControlCenterSnapshot { VolumePercent = 37 });
        Set(runtime, "_controlMonitor", source.Object);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.WidgetMonitorUpdated += (_, _) => received.TrySetResult();
        var widget = runtime.AddWidget(DesktopWidgetKind.ControlCenter);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(37, widget.Controls!.VolumePercent);
    }

    [Fact]
    public async Task SelectingDisplayCancelsQueuedBrightnessAndRoutesNewWritesToTheSelectedScreen()
    {
        var runtime = Fixture(); using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { }); Set(runtime, "_saveTimer", timer);
        var second = new MonitorLayout { Id = "second", DeviceName = "second", ModelName = "AOC 27B30H6", IsPrimary = false,
            Bounds = new(1000, 0, 800, 600), WorkArea = new(1000, 0, 800, 600), PixelBounds = new(1000, 0, 800, 600), PixelWorkArea = new(1000, 0, 800, 600) };
        Set(runtime, "<Monitors>k__BackingField", new[] { runtime.Monitors[0], second });
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.ControlCenter, MonitorId = "test", Controls = new() { BrightnessPercent = 20 } }; runtime.State.Widgets.Add(widget);
        var source = new Mock<IWidgetControlCenterMonitor>(); source.SetupProperty(s => s.BrightnessDisplayId, "");
        source.Setup(s => s.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ControlCenterSnapshot { BrightnessPercent = 60 });
        source.Setup(s => s.ReadFast()).Returns(new ControlCenterSnapshot { BrightnessPercent = 40 });
        var writes = new ConcurrentQueue<(string, double)>();
        source.Setup(s => s.SetBrightness(It.IsAny<double>())).Returns((double value) => { writes.Enqueue((source.Object.BrightnessDisplayId, value)); return true; });
        Set(runtime, "_controlMonitor", source.Object);
        runtime.SetControlBrightness(70);
        await runtime.SelectControlBrightnessDisplayAsync(widget, "second");
        Assert.Equal("second", widget.ControlBrightnessDisplayId); Assert.Equal(60, widget.Controls!.BrightnessPercent);
        Assert.Contains("AOC 27B30H6", runtime.ControlBrightnessDisplays[1].Label);
        Assert.Contains("800 × 600", runtime.ControlBrightnessDisplays[1].Label);
        Assert.Contains("AOC 27B30H6", runtime.ControlBrightnessDisplayLabel(widget));
        runtime.SetControlBrightness(40);
        await ((Task)runtime.GetType().GetField("_controlWriteTask", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(("second", 40d), Assert.Single(writes));
        await runtime.SelectControlBrightnessDisplayAsync(widget, "missing"); Assert.Equal("second", widget.ControlBrightnessDisplayId);
    }
    [Theory]
    [InlineData("volume")]
    [InlineData("brightness")]
    [InlineData("mute")]
    public async Task RemovingControlCenterPreventsQueuedLevelWrites(string control)
    {
        var runtime = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.ControlCenter, Controls = new() { VolumePercent = 20, BrightnessPercent = 20, VolumeMuted = false } };
        runtime.State.Widgets.Add(widget);
        var source = new Mock<IWidgetControlCenterMonitor>();
        source.Setup(s => s.SetVolume(It.IsAny<double>())).Returns(true);
        source.Setup(s => s.SetBrightness(It.IsAny<double>())).Returns(true);
        source.Setup(s => s.SetMuted(It.IsAny<bool>())).Returns(true);
        source.Setup(s => s.ReadFast()).Returns(new ControlCenterSnapshot());
        Set(runtime, "_controlMonitor", source.Object);

        if (control == "volume") runtime.SetControlVolume(70);
        else if (control == "brightness") runtime.SetControlBrightness(70);
        else runtime.SetControlMuted(true);
        runtime.RemoveWidget(widget);
        await ((Task)runtime.GetType().GetField("_controlWriteTask", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!).WaitAsync(TimeSpan.FromSeconds(5));

        source.Verify(s => s.SetVolume(It.IsAny<double>()), Times.Never);
        source.Verify(s => s.SetBrightness(It.IsAny<double>()), Times.Never);
        source.Verify(s => s.SetMuted(It.IsAny<bool>()), Times.Never);
        source.Verify(s => s.ReadFast(), Times.Never);
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("paused")]
    [InlineData("disposed")]
    public async Task InactiveControlCenterPreventsOutputChangesWaitingForHardware(string state)
    {
        var runtime = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        using var hardware = new SemaphoreSlim(0, 1);
        Set(runtime, "_controlIoGate", hardware);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.ControlCenter };
        runtime.State.Widgets.Add(widget);
        var source = new Mock<IWidgetControlCenterMonitor>();
        source.Setup(s => s.SetOutputDevice("speakers")).Returns(true);
        source.Setup(s => s.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ControlCenterSnapshot());
        Set(runtime, "_controlMonitor", source.Object);

        var command = runtime.SetControlOutputAsync("speakers");
        try
        {
            if (state == "removed") runtime.RemoveWidget(widget);
            else if (state == "paused") Set(runtime, "<IsPaused>k__BackingField", true);
            else Set(runtime, "_disposed", true);
        }
        finally { hardware.Release(); }
        await command.WaitAsync(TimeSpan.FromSeconds(5));

        source.Verify(s => s.SetOutputDevice(It.IsAny<string>()), Times.Never);
        source.Verify(s => s.ReadAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectingDisplayCancelsBrightnessAlreadyWaitingForHardwareAndPreservesVolume(bool returnToOriginalDisplay)
    {
        var runtime = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        using var hardware = new SemaphoreSlim(0, 1);
        Set(runtime, "_controlIoGate", hardware);
        var second = new MonitorLayout { Id = "second", DeviceName = "second", Bounds = new(1000, 0, 800, 600), WorkArea = new(1000, 0, 800, 600),
            PixelBounds = new(1000, 0, 800, 600), PixelWorkArea = new(1000, 0, 800, 600) };
        Set(runtime, "<Monitors>k__BackingField", new[] { runtime.Monitors[0], second });
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.ControlCenter, MonitorId = "test", Controls = new() { VolumePercent = 20, BrightnessPercent = 20 } };
        runtime.State.Widgets.Add(widget);
        var source = new Mock<IWidgetControlCenterMonitor>();
        source.SetupProperty(s => s.BrightnessDisplayId, "test");
        source.Setup(s => s.SetVolume(35)).Returns(true);
        source.Setup(s => s.SetMuted(false)).Returns(true);
        source.Setup(s => s.SetBrightness(It.IsAny<double>())).Returns(true);
        source.Setup(s => s.ReadFast()).Returns(() => new ControlCenterSnapshot { VolumePercent = 35, BrightnessPercent = source.Object.BrightnessDisplayId == "second" ? 60 : 20 });
        source.Setup(s => s.ReadAsync(It.IsAny<CancellationToken>())).Returns(() => Task.FromResult(new ControlCenterSnapshot
            { VolumePercent = 35, BrightnessPercent = source.Object.BrightnessDisplayId == "second" ? 60 : 20 }));
        Set(runtime, "_controlMonitor", source.Object);

        runtime.SetControlVolume(35);
        runtime.SetControlBrightness(70);
        Task selection = Task.CompletedTask;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (runtime.GetType().GetField("_pendingControlBrightness", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime) is not null)
                await Task.Delay(10, timeout.Token);
            selection = runtime.SelectControlBrightnessDisplayAsync(widget, "second");
            if (returnToOriginalDisplay)
                selection = Task.WhenAll(selection, runtime.SelectControlBrightnessDisplayAsync(widget, "test"));
        }
        finally { hardware.Release(); }
        await selection.WaitAsync(TimeSpan.FromSeconds(5));
        await ((Task)runtime.GetType().GetField("_controlWriteTask", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!).WaitAsync(TimeSpan.FromSeconds(5));

        source.Verify(s => s.SetBrightness(It.IsAny<double>()), Times.Never);
        source.Verify(s => s.SetVolume(35), Times.Once);
        source.Verify(s => s.SetMuted(false), Times.Once);
        Assert.Equal(returnToOriginalDisplay ? "test" : "second", widget.ControlBrightnessDisplayId);
        Assert.Equal(returnToOriginalDisplay ? 20 : 60, widget.Controls!.BrightnessPercent);
    }

    [Fact]
    public void NewlyAddedControlCenterHasASupportedSizeAndSurvivesNormalization()
    {
        var runtime = Fixture(); using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { }); Set(runtime, "_saveTimer", timer);
        var widget = runtime.AddWidget(DesktopWidgetKind.ControlCenter);
        Assert.Contains(widget.Size, DesktopWidgets.SupportedSizes(widget));
        Assert.Equal(DesktopWidgetSize.Panel, widget.Size);
        var bounds = widget.Bounds; DesktopWidgets.Normalize(runtime.State);
        Assert.Equal(bounds, widget.Bounds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(100)]
    public async Task VolumeChangesClearSystemMute(double requestedVolume)
    {
        var runtime = Fixture();
        var widget = new DesktopWidget
        {
            Kind = DesktopWidgetKind.ControlCenter,
            Controls = new() { VolumePercent = 20, VolumeMuted = true }
        };
        runtime.State.Widgets.Add(widget);
        var source = new Mock<IWidgetControlCenterMonitor>();
        double volume = 20;
        var muted = true;
        source.Setup(s => s.SetVolume(It.IsAny<double>())).Returns((double value) => { volume = value; return true; });
        source.Setup(s => s.SetMuted(It.IsAny<bool>())).Returns((bool value) => { muted = value; return true; });
        source.Setup(s => s.ReadFast()).Returns(() => new ControlCenterSnapshot { VolumePercent = volume, VolumeMuted = muted });
        Set(runtime, "_controlMonitor", source.Object);

        runtime.SetControlVolume(requestedVolume);
        var immediate = widget.Controls;
        await ((Task)runtime.GetType().GetField("_controlWriteTask", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(muted);
        Assert.False(immediate!.VolumeMuted);
        Assert.False(widget.Controls!.VolumeMuted);
        Assert.Equal(requestedVolume, widget.Controls.VolumePercent);
        source.Verify(s => s.SetMuted(false), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MuteButtonPreservesTheVolumeAndMedia(bool initialMuted)
    {
        var runtime = Fixture();
        var media = new ControlCenterMedia("Track", "Artist", "", true, true, true, true);
        var widget = new DesktopWidget
        {
            Kind = DesktopWidgetKind.ControlCenter, MonitorId = "test", Size = DesktopWidgetSize.Panel,
            Bounds = new(100, 100, 372, 292),
            Controls = new() { VolumePercent = 42, VolumeMuted = initialMuted, Media = media }
        };
        runtime.State.Widgets.Add(widget);
        var source = new Mock<IWidgetControlCenterMonitor>();
        var muted = initialMuted;
        source.Setup(s => s.SetMuted(It.IsAny<bool>())).Returns((bool value) => { muted = value; return true; });
        source.Setup(s => s.ReadFast()).Returns(() => new ControlCenterSnapshot { VolumePercent = 42, VolumeMuted = muted });
        Set(runtime, "_controlMonitor", source.Object);

        OnSta(() =>
        {
            using var form = new DesktopBoxForm(runtime, runtime.Monitors[0]);
            var body = ControlCenterPresentation.Body(372, 292, runtime.State.Settings.Appearance.CornerRadius);
            var button = ControlCenterPresentation.VolumeMuteButton(body);
            var point = new PointF((float)(100 + button.X + button.Width / 2), (float)(100 + button.Y + button.Height / 2));
            var isSlider = (bool)typeof(DesktopBoxForm).GetMethod("IsControlCenterDragTarget", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(form, [widget, point])!;
            Assert.False(isSlider);
            Assert.True((bool)typeof(DesktopBoxForm).GetMethod("HandleControlCenterClick", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(form, [widget, point])!);
            Assert.Equal(!initialMuted, widget.Controls!.VolumeMuted);
            Assert.Equal(42, widget.Controls.VolumePercent);
        });
        await ((Task)runtime.GetType().GetField("_controlWriteTask", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(!initialMuted, muted);
        Assert.Equal(muted, widget.Controls!.VolumeMuted);
        Assert.Equal(42, widget.Controls.VolumePercent);
        Assert.Same(media, widget.Controls.Media);
        source.Verify(s => s.SetVolume(It.IsAny<double>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LatestMuteAndVolumeIntentWinsDuringASlowWrite(bool muteLast)
    {
        var runtime = Fixture();
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.ControlCenter, Controls = new() { VolumePercent = 20, VolumeMuted = false } };
        runtime.State.Widgets.Add(widget);
        var source = new Mock<IWidgetControlCenterMonitor>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        double volume = 20;
        var muted = false;
        source.Setup(s => s.SetVolume(It.IsAny<double>())).Returns((double value) =>
        {
            if (value == 30) { started.TrySetResult(); release.Wait(TimeSpan.FromSeconds(5)); }
            volume = value;
            return true;
        });
        source.Setup(s => s.SetMuted(It.IsAny<bool>())).Returns((bool value) => { muted = value; return true; });
        source.Setup(s => s.ReadFast()).Returns(() => new ControlCenterSnapshot { VolumePercent = volume, VolumeMuted = muted });
        Set(runtime, "_controlMonitor", source.Object);

        runtime.SetControlVolume(30);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            runtime.SetControlMuted(true);
            if (!muteLast) runtime.SetControlVolume(60);
        }
        finally { release.Set(); }
        await ((Task)runtime.GetType().GetField("_controlWriteTask", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(muteLast, muted);
        Assert.Equal(muteLast ? 30 : 60, volume);
        Assert.Equal(muted, widget.Controls!.VolumeMuted);
        Assert.Equal(volume, widget.Controls.VolumePercent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedMuteWriteRestoresTheReportedState(bool requestedMute)
    {
        var runtime = Fixture();
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.ControlCenter, Controls = new() { VolumePercent = 42, VolumeMuted = !requestedMute } };
        runtime.State.Widgets.Add(widget);
        var source = new Mock<IWidgetControlCenterMonitor>();
        source.Setup(s => s.SetMuted(requestedMute)).Returns(false);
        source.Setup(s => s.ReadFast()).Returns(new ControlCenterSnapshot { VolumePercent = 42, VolumeMuted = !requestedMute });
        Set(runtime, "_controlMonitor", source.Object);

        runtime.SetControlMuted(requestedMute);
        var immediate = widget.Controls;
        await ((Task)runtime.GetType().GetField("_controlWriteTask", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(requestedMute, immediate!.VolumeMuted);
        Assert.Equal(!requestedMute, widget.Controls!.VolumeMuted);
        Assert.Equal(42, widget.Controls.VolumePercent);
        Assert.NotEmpty(widget.MonitorError);
    }

    [Fact]
    public async Task FailedVolumeWriteKeepsTheSystemMuted()
    {
        var runtime = Fixture();
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.ControlCenter, Controls = new() { VolumePercent = 90, VolumeMuted = true } };
        runtime.State.Widgets.Add(widget);
        var source = new Mock<IWidgetControlCenterMonitor>();
        source.Setup(s => s.SetVolume(10)).Returns(false);
        source.Setup(s => s.ReadFast()).Returns(new ControlCenterSnapshot { VolumePercent = 90, VolumeMuted = true });
        Set(runtime, "_controlMonitor", source.Object);

        runtime.SetControlVolume(10);
        await ((Task)runtime.GetType().GetField("_controlWriteTask", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!).WaitAsync(TimeSpan.FromSeconds(5));

        source.Verify(s => s.SetMuted(It.IsAny<bool>()), Times.Never);
        Assert.True(widget.Controls!.VolumeMuted);
        Assert.Equal(90, widget.Controls.VolumePercent);
        Assert.NotEmpty(widget.MonitorError);
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(500, 200)]
    public async Task VolumeTrackClickUsesTheWidgetsPositionAndUnmutes(double x, double y)
    {
        var runtime = Fixture();
        var widget = new DesktopWidget
        {
            Kind = DesktopWidgetKind.ControlCenter, MonitorId = "test", Size = DesktopWidgetSize.Panel,
            Bounds = new(x, y, 372, 292), Controls = new() { VolumePercent = 20, VolumeMuted = true }
        };
        runtime.State.Widgets.Add(widget);
        var source = new Mock<IWidgetControlCenterMonitor>();
        source.Setup(s => s.SetVolume(It.IsAny<double>())).Returns(true);
        source.Setup(s => s.SetMuted(false)).Returns(true);
        source.Setup(s => s.ReadFast()).Returns(new ControlCenterSnapshot { VolumePercent = 75, VolumeMuted = false });
        Set(runtime, "_controlMonitor", source.Object);

        OnSta(() =>
        {
            using var form = new DesktopBoxForm(runtime, runtime.Monitors[0]);
            var body = ControlCenterPresentation.Body(372, 292, runtime.State.Settings.Appearance.CornerRadius);
            var sliders = ControlCenterPresentation.SliderArea(body);
            var point = new PointF((float)(x + sliders.X + (sliders.Width - 14) / 2 * 0.75), (float)(y + sliders.Y + 26));
            Assert.True((bool)typeof(DesktopBoxForm).GetMethod("HandleControlCenterClick", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(form, [widget, point])!);
            Assert.Equal(75, widget.Controls!.VolumePercent!.Value, 2);
            Assert.False(widget.Controls.VolumeMuted);
        });
        await ((Task)runtime.GetType().GetField("_controlWriteTask", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!).WaitAsync(TimeSpan.FromSeconds(5));
        source.Verify(s => s.SetMuted(false), Times.Once);
    }

    [Fact]
    public async Task HardwareWritesAreCoalescedAndReadBackOnlyAppliesOnTheUiDispatcher()
    {
        var runtime = Fixture(); var ui = new ConcurrentQueue<Action>(); Set(runtime, "_beginInvoke", (Action<Action>)(ui.Enqueue));
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.ControlCenter, Controls = new() { BrightnessPercent = 20 } };
        runtime.State.Widgets.Add(widget);
        var source = new Mock<IWidgetControlCenterMonitor>();
        var requested = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finish = new ManualResetEventSlim();
        source.Setup(s => s.SetBrightness(It.IsAny<double>())).Returns((double value) => { requested.TrySetResult(value); finish.Wait(TimeSpan.FromSeconds(5)); return true; });
        source.Setup(s => s.ReadFast()).Returns(new ControlCenterSnapshot { BrightnessPercent = 73 });
        Set(runtime, "_controlMonitor", source.Object);
        for (var value = 30; value <= 80; value++) runtime.SetControlBrightness(value);
        Assert.Equal(80, widget.Controls!.BrightnessPercent);
        try { Assert.Equal(80, await requested.Task.WaitAsync(TimeSpan.FromSeconds(5))); }
        finally { finish.Set(); }
        await ((Task)runtime.GetType().GetField("_controlWriteTask", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!).WaitAsync(TimeSpan.FromSeconds(5));
        source.Verify(s => s.SetBrightness(80), Times.Once);
        source.Verify(s => s.SetBrightness(It.IsAny<double>()), Times.Once);
        Assert.Equal(80, widget.Controls.BrightnessPercent);
        Assert.True(ui.TryDequeue(out var apply)); apply!();
        Assert.Equal(73, widget.Controls.BrightnessPercent);
    }

    [Fact]
    public async Task CommandsReadBackOnUiAndDiscardResultsForRemovedWidgets()
    {
        var runtime = Fixture(); var ui = new ConcurrentQueue<Action>(); Set(runtime, "_beginInvoke", (Action<Action>)(ui.Enqueue));
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.ControlCenter, Controls = new() { OutputDeviceId = "old" } }; runtime.State.Widgets.Add(widget);
        var source = new Mock<IWidgetControlCenterMonitor>();
        source.Setup(s => s.SetOutputDevice("new")).Returns(true);
        source.Setup(s => s.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ControlCenterSnapshot { OutputDeviceId = "new" });
        Set(runtime, "_controlMonitor", source.Object);
        await runtime.SetControlOutputAsync("new");
        Assert.Equal("old", widget.Controls!.OutputDeviceId);
        Assert.True(ui.TryDequeue(out var apply)); apply!();
        Assert.Equal("new", widget.Controls.OutputDeviceId);
        await runtime.SetControlOutputAsync("new"); runtime.State.Widgets.Clear(); widget.Controls = new() { OutputDeviceId = "removed" };
        Assert.True(ui.TryDequeue(out apply)); apply!(); Assert.Equal("removed", widget.Controls.OutputDeviceId);
    }

    [Fact]
    public async Task SliderFlushesTheLastValueArrivingDuringASlowWriteAndRollsBackFailures()
    {
        var runtime = Fixture(); var ui = new ConcurrentQueue<Action>(); Set(runtime, "_beginInvoke", (Action<Action>)(ui.Enqueue));
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.ControlCenter, Controls = new() { BrightnessPercent = 20 } }; runtime.State.Widgets.Add(widget);
        var source = new Mock<IWidgetControlCenterMonitor>();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var writes = new ConcurrentQueue<double>(); double actual = 20;
        source.Setup(s => s.SetBrightness(It.IsAny<double>())).Returns((double value) =>
        {
            writes.Enqueue(value);
            if (value == 30) { first.TrySetResult(); release.Wait(TimeSpan.FromSeconds(5)); actual = value; return true; }
            return false;
        });
        source.Setup(s => s.ReadFast()).Returns(() => new ControlCenterSnapshot { BrightnessPercent = actual });
        Set(runtime, "_controlMonitor", source.Object);
        runtime.SetControlBrightness(30);
        try { await first.Task.WaitAsync(TimeSpan.FromSeconds(5)); runtime.SetControlBrightness(50); runtime.SetControlBrightness(80); }
        finally { release.Set(); }
        await ((Task)runtime.GetType().GetField("_controlWriteTask", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 30d, 80d }, writes.ToArray());
        Assert.True(ui.TryDequeue(out var old)); old!(); Assert.Equal(80, widget.Controls!.BrightnessPercent);
        Assert.True(ui.TryDequeue(out var latest)); latest!();
        Assert.Equal(30, widget.Controls.BrightnessPercent); Assert.NotEmpty(widget.MonitorError);
    }

    [Fact]
    public void OutputMenuClickDispatchesTheChosenDevice() => OnSta(() =>
    {
        var choices = new List<string>();
        using var menu = DesktopBoxForm.BuildControlCenterOutputMenu(new ControlCenterSnapshot { OutputDevices = [new("a", "A", true), new("b", "B", false)] },
            selectOutput: id => { choices.Add(id); return Task.CompletedTask; });
        menu.Items[1].PerformClick(); Assert.Equal("b", Assert.Single(choices));
    });
    [Fact]
    public void ProcessRankingDirectionChangesWithoutResettingSamplesAndIgnoresInvalidRequests()
    {
        var runtime = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        var network = new WidgetNetworkSnapshot(DateTimeOffset.Now)
        { Processes = new(NetworkProcessStatus.Ready, [new(100, "browser", 1000, 200)]) };
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Network, Network = network };
        runtime.State.Widgets.Add(widget);
        var changes = 0; runtime.Changed += (_, _) => changes++;
        runtime.SetNetworkProcessSort(widget, NetworkProcessSort.Upload);
        Assert.Equal(NetworkProcessSort.Upload, widget.NetworkProcessSort);
        Assert.Same(network, widget.Network);
        Assert.Equal(1, changes);
        runtime.SetNetworkProcessSort(widget, NetworkProcessSort.Upload);
        runtime.SetNetworkProcessSort(widget, (NetworkProcessSort)999);
        runtime.SetNetworkProcessSort(new DesktopWidget { Kind = DesktopWidgetKind.Network }, NetworkProcessSort.Upload);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void ProcessRankingButtonsSwitchDirectionOnTheDesktop() => OnSta(() =>
    {
        var runtime = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Network, MonitorId = "test", Bounds = new(100, 100, 372, 180) };
        runtime.State.Widgets.Add(widget);
        using var form = new DesktopBoxForm(runtime, runtime.Monitors[0]);
        var fields = WidgetMonitorPresentation.NetworkFields(WidgetMonitorPresentation.Body(372, 180,
            runtime.State.Settings.Appearance.ResolveWidgetCornerRadius()));
        var click = typeof(DesktopBoxForm).GetMethod("HandleWidgetClick", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(click);
        foreach (var (rect, direction) in new[] { (fields.SortUpload, NetworkProcessSort.Upload), (fields.SortDownload, NetworkProcessSort.Download) })
        {
            click!.Invoke(form, [widget, new PointF((float)(100 + rect.X + rect.Width / 2), (float)(100 + rect.Y + rect.Height / 2))]);
            Assert.Equal(direction, widget.NetworkProcessSort);
        }
        widget.NetworkDisplayMode = NetworkWidgetDisplayMode.Curve;
        click!.Invoke(form, [widget, new PointF((float)(100 + fields.SortUpload.X + 2), (float)(100 + fields.SortUpload.Y + 2))]);
        Assert.Equal(NetworkProcessSort.Download, widget.NetworkProcessSort);
    });

    [Fact]
    public void NetworkMenuShowsSelectedRankingDirection()
    {
        using var menu = DesktopBoxForm.BuildMonitorContextMenu(new DesktopWidget
            { Kind = DesktopWidgetKind.Network, NetworkProcessSort = NetworkProcessSort.Upload });
        var ranking = Assert.Single(menu.Items.OfType<ToolStripMenuItem>().Where(item => item.Text == "进程排行"));
        Assert.Equal(["按下载排序", "按上传排序"], ranking.DropDownItems.OfType<ToolStripMenuItem>().Select(item => item.Text));
        Assert.False(((ToolStripMenuItem)ranking.DropDownItems[0]).Checked);
        Assert.True(((ToolStripMenuItem)ranking.DropDownItems[1]).Checked);
    }

    [Fact]
    public void SwitchingNetworkStylesResizesAndClampsWithoutLosingLiveData()
    {
        var runtime = Fixture(); using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { }); Set(runtime, "_saveTimer", timer);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Network, MonitorId = "test", Size = DesktopWidgetSize.Large,
            Bounds = new(900, 630, 372, 180), Network = new(DateTimeOffset.Now) { InterfaceId = "a", IsConnected = true } };
        widget.NetworkHistory.Add(new(DateTimeOffset.Now, 100, 50)); runtime.State.Widgets.Add(widget);
        var changed = 0; runtime.Changed += (_, _) => changed++;
        runtime.SetNetworkDisplayMode(widget, NetworkWidgetDisplayMode.Speeds);
        Assert.Equal(DesktopWidgetSize.Small, widget.Size); Assert.Equal(180, widget.Bounds.Width); Assert.Equal(84, widget.Bounds.Height);
        Assert.True(widget.Bounds.X + widget.Bounds.Width <= 1000 && widget.Bounds.Y + widget.Bounds.Height <= 700);
        runtime.ResizeWidget(widget, DesktopWidgetSize.Wide); Assert.Equal(372, widget.Bounds.Width);
        runtime.SetNetworkDisplayMode(widget, NetworkWidgetDisplayMode.Curve);
        Assert.Equal(DesktopWidgetSize.Large, widget.Size); Assert.Equal(180, widget.Bounds.Height);
        Assert.NotNull(widget.Network); Assert.Single(widget.NetworkHistory); Assert.Equal(3, changed);
        runtime.SetNetworkDisplayMode(widget, NetworkWidgetDisplayMode.Curve); Assert.Equal(3, changed);
        runtime.ResizeWidget(widget, DesktopWidgetSize.Small); Assert.Equal(DesktopWidgetSize.Large, widget.Size);
    }

    [Fact]
    public void UnsupportedDisplayModeAndInsufficientWorkAreaDoNotPartiallyChangeTheWidget()
    {
        var runtime = Fixture();
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Network, MonitorId = "test", Size = DesktopWidgetSize.Small,
            NetworkDisplayMode = NetworkWidgetDisplayMode.Speeds, Bounds = new(0, 0, 180, 84) };
        runtime.State.Widgets.Add(widget);
        runtime.SetNetworkDisplayMode(widget, (NetworkWidgetDisplayMode)999);
        Set(runtime, "<Monitors>k__BackingField", new[] { new MonitorLayout { Id = "test", DeviceName = "test", IsPrimary = true,
            Bounds = new(0, 0, 200, 100), WorkArea = new(0, 0, 200, 100), PixelBounds = new(0, 0, 200, 100), PixelWorkArea = new(0, 0, 200, 100) } });
        Assert.Throws<InvalidOperationException>(() => runtime.SetNetworkDisplayMode(widget, NetworkWidgetDisplayMode.Curve));
        Assert.Equal(NetworkWidgetDisplayMode.Speeds, widget.NetworkDisplayMode); Assert.Equal(DesktopWidgetSize.Small, widget.Size);
        Assert.Equal(new LayoutRect(0, 0, 180, 84), widget.Bounds);
    }

    [Fact]
    public async Task PublicIpIsOnlyRequestedExplicitlyAndConcurrentClicksShareOneRequest()
    {
        var runtime = Fixture(); using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { }); Set(runtime, "_saveTimer", timer);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0; var changed = 0; runtime.Changed += (_, _) => changed++;
        Set(runtime, "_publicIpQuery", (Func<CancellationToken, Task<string>>)(_ => { requests++; return completion.Task; }));
        var widget = runtime.AddWidget(DesktopWidgetKind.Network);
        Assert.Same(widget, runtime.AddWidget(DesktopWidgetKind.Network)); Assert.Equal(0, requests);
        widget.Network = new(DateTimeOffset.Now) { IsConnected = true, InterfaceId = "test" };
        var saves = changed;
        var request = runtime.QueryWidgetPublicIpAsync(widget);
        Assert.True(widget.IsPublicIpLoading); Assert.Equal(1, requests);
        await runtime.QueryWidgetPublicIpAsync(widget); Assert.Equal(1, requests);
        completion.SetResult("203.0.113.18"); await request;
        Assert.Equal("203.0.113.18", widget.PublicIp); Assert.False(widget.IsPublicIpLoading);
        Assert.Equal(saves, changed);
    }

    [Theory]
    [InlineData("remove")] [InlineData("switch")] [InlineData("disconnect")]
    public async Task PublicIpResultCannotReappearAfterRemovalInterfaceSwitchOrDisconnect(string action)
    {
        var runtime = Fixture(); using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { }); Set(runtime, "_saveTimer", timer);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Set(runtime, "_publicIpQuery", (Func<CancellationToken, Task<string>>)(_ => completion.Task));
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Network, Network = new(DateTimeOffset.Now) { IsConnected = true, InterfaceId = "old" } };
        runtime.State.Widgets.Add(widget);
        var request = runtime.QueryWidgetPublicIpAsync(widget);
        if (action == "remove") runtime.RemoveWidget(widget);
        else if (action == "disconnect") widget.Network = widget.Network with { IsConnected = false };
        else widget.Network = widget.Network with { InterfaceId = "new" };
        completion.SetResult("203.0.113.18"); await request;
        Assert.Empty(widget.PublicIp); Assert.False(widget.IsPublicIpLoading);
    }

    [Fact]
    public async Task DisconnectedNetworkDoesNotQueryAndFailedQueriesCanRetry()
    {
        var runtime = Fixture(); var requests = 0;
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Network }; runtime.State.Widgets.Add(widget);
        Set(runtime, "_publicIpQuery", (Func<CancellationToken, Task<string>>)(_ =>
            ++requests == 1 ? Task.FromException<string>(new HttpRequestException("offline")) : Task.FromResult("203.0.113.19")));
        await runtime.QueryWidgetPublicIpAsync(widget); Assert.Equal(0, requests); Assert.NotEmpty(widget.PublicIpError);
        widget.Network = new(DateTimeOffset.Now) { IsConnected = true, InterfaceId = "adapter" };
        await runtime.QueryWidgetPublicIpAsync(widget); Assert.False(widget.IsPublicIpLoading); Assert.NotEmpty(widget.PublicIpError);
        await runtime.QueryWidgetPublicIpAsync(widget); Assert.Equal("203.0.113.19", widget.PublicIp); Assert.Empty(widget.PublicIpError);
    }

    [Fact]
    public async Task PublicIpTransportTimeoutShowsRetryState()
    {
        var runtime = Fixture();
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Network, Network = new(DateTimeOffset.Now) { IsConnected = true, InterfaceId = "adapter" } };
        runtime.State.Widgets.Add(widget);
        Set(runtime, "_publicIpQuery", (Func<CancellationToken, Task<string>>)(_ => Task.FromException<string>(new TaskCanceledException("HTTP timeout"))));
        await runtime.QueryWidgetPublicIpAsync(widget);
        Assert.False(widget.IsPublicIpLoading); Assert.NotEmpty(widget.PublicIpError); Assert.Empty(widget.PublicIp);
    }

        // Every style must be offered in the menu, and switching to the curve-only
    // style has to land on the large card, which is what its layout assumes.
    [Fact]
    public void CurveOnlyStyleIsOfferedAndSwitchesToALargeCard()
    {
        var runtime = Fixture(); using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { }); Set(runtime, "_saveTimer", timer);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Network, MonitorId = "test", Size = DesktopWidgetSize.Small,
            Bounds = new(100, 100, 180, 84), Network = new(DateTimeOffset.Now) { IsConnected = true } };
        runtime.State.Widgets.Add(widget);

        using (var menu = DesktopBoxForm.BuildMonitorContextMenu(widget))
        {
            var styles = menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "显示样式");
            var labels = styles.DropDownItems.OfType<ToolStripMenuItem>().Select(item => item.Text).ToArray();
            Assert.Contains("网速曲线", labels);
            Assert.Equal(DesktopWidgets.OfferedNetworkDisplayModes.Count, labels.Length);
            Assert.Equal(["详细信息", "网速文本", "网速曲线"], labels);
        }

        runtime.SetNetworkDisplayMode(widget, NetworkWidgetDisplayMode.Curve);

        Assert.Equal(NetworkWidgetDisplayMode.Curve, widget.NetworkDisplayMode);
        Assert.Equal(DesktopWidgetSize.Large, widget.Size);
        Assert.Equal(372, widget.Bounds.Width);
        Assert.Equal(180, widget.Bounds.Height);
    }

    // The output row opens a list rather than cycling: with three devices the
    // user picks the one they want instead of clicking until it comes round.
    [Fact]
    public void OutputDeviceMenuListsEveryEndpointAndMarksTheCurrentOne()
    {
        var snapshot = new ControlCenterSnapshot
        {
            OutputDeviceId = "b",
            OutputDevices = [new("a", "耳机", false), new("b", "扬声器", true), new("c", "显示器", false)]
        };

        using var menu = DesktopBoxForm.BuildControlCenterOutputMenu(snapshot);

        var items = menu.Items.OfType<ToolStripMenuItem>().ToArray();
        Assert.Equal(["耳机", "扬声器", "显示器"], items.Select(item => item.Text).ToArray());
        // Exactly one tick, on the device the card is currently showing.
        Assert.Single(items, item => item.Checked);
        Assert.Equal("扬声器", items.Single(item => item.Checked).Text);
    }

    [Fact]
    public void OutputDeviceMenuIsEmptyWhenNoEndpointIsKnown()
    {
        using var menu = DesktopBoxForm.BuildControlCenterOutputMenu(new ControlCenterSnapshot());

        Assert.Empty(menu.Items);
    }

    // Disposing a ContextMenuStrip synchronously from its own Closed handler
    // leaves a disposed drop-down behind that ToolStripManager still references,
    // which crashed the app on the next click. Every menu in this project defers
    // the dispose; this test pins the contract for the output picker too.
    [Fact]
    public void OutputDeviceSelectionHandsOffWithoutThrow()
    {
        var snapshot = new ControlCenterSnapshot
        {
            OutputDeviceId = "a",
            OutputDevices = [new("a", "耳机", true), new("b", "扬声器", false)]
        };
        using var menu = DesktopBoxForm.BuildControlCenterOutputMenu(snapshot);

        var item = menu.Items.OfType<ToolStripMenuItem>().Last();
        Assert.Equal("扬声器", item.Text);

        // Closing then disposing is the deferred path the form uses; disposing
        // while Closed is still running is what threw.
        menu.Close();
        menu.Dispose();
    }

    [Fact]
    public void QuickActionTopEdgesRemainClickableBelowTheCompactDragStrip() => OnSta(() =>
    {
        var runtime = Fixture();
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.ControlCenter, MonitorId = "test", Size = DesktopWidgetSize.Panel,
            Bounds = new(100, 100, 372, 292), Controls = new() { WiFiEnabled = true, BluetoothEnabled = true, NightLightEnabled = false } };
        runtime.State.Widgets.Add(widget);
        using var form = new DesktopBoxForm(runtime, runtime.Monitors[0]);
        var body = ControlCenterPresentation.Body(widget.Bounds.Width, widget.Bounds.Height, runtime.State.Settings.Appearance.CornerRadius);
        var actions = ControlQuickActions.For(widget);
        var mouseDown = typeof(DesktopBoxForm).GetMethod("HandleWidgetMouseDown", BindingFlags.NonPublic | BindingFlags.Instance)!;

        for (var index = 0; index < actions.Count; index++)
        {
            var tile = ControlCenterPresentation.QuickAction(body, index, actions.Count);
            var point = new PointF((float)(widget.Bounds.X + tile.X + tile.Width - 4), (float)(widget.Bounds.Y + tile.Y + 2));
            Assert.True((bool)mouseDown.Invoke(form, [new MouseEventArgs(MouseButtons.Left, 1, (int)point.X, (int)point.Y, 0), point])!);
            Assert.Same(widget, typeof(DesktopBoxForm).GetField("_pressedWidget", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form));
            Assert.False((bool)typeof(DesktopBoxForm).GetField("_widgetCanDrag", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!);
        }
    });

    // A slider that can only be clicked is not a slider. Pressing one must capture
    // the pointer, or the move falls through to the drag branch and the whole card
    // slides away instead of the level changing.
    [Fact]
    public void SlidersCaptureThePointerAndFollowItWhileDragging() => OnSta(() =>
    {
        var runtime = Fixture(); using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { }); Set(runtime, "_saveTimer", timer);
        var widget = new DesktopWidget
        {
            Kind = DesktopWidgetKind.ControlCenter, MonitorId = "test", Size = DesktopWidgetSize.Panel,
            Bounds = new(100, 100, 372, 292),
            Controls = new ControlCenterSnapshot { VolumePercent = 20, BrightnessPercent = 20 }
        };
        runtime.State.Widgets.Add(widget);
        using var form = new DesktopBoxForm(runtime, runtime.Monitors.First());

        var body = ControlCenterPresentation.Body(372, 292, 8);
        var sliders = ControlCenterPresentation.SliderArea(body);
        var half = (sliders.Width - 14) / 2;

        var captures = (bool)typeof(DesktopBoxForm)
            .GetMethod("IsControlCenterDragTarget", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(form, [widget, new PointF((float)(100 + sliders.X + half / 2), (float)(100 + sliders.Y + 10))])!;
        Assert.True(captures);

        var update = typeof(DesktopBoxForm)
            .GetMethod("UpdateControlCenterDrag", BindingFlags.NonPublic | BindingFlags.Instance)!;
        typeof(DesktopBoxForm).GetField("_controlPressedWidgetId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(form, (Guid?)widget.Id);
        typeof(DesktopBoxForm).GetField("_controlPressedTarget", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(form, ControlCenterPresentation.ControlCenterTarget.VolumeSlider);

        foreach (var fraction in new[] { 0.25, 0.75 })
        {
            var x = (float)(100 + sliders.X + half * fraction);
            update.Invoke(form, [widget, new PointF(x, (float)(100 + sliders.Y + 10))]);
            // The level tracks the pointer rather than staying where it was pressed.
            Assert.Equal(fraction * 100, widget.Controls!.VolumePercent!.Value, 1);
        }
    });

    // The model names are the reason the submenu exists; they have to reach it.
    [Fact]
    public void HardwareSubmenuListsTheModelNamesItKnows()
    {
        var widget = new DesktopWidget
        {
            Kind = DesktopWidgetKind.Performance,
            Performance = new WidgetPerformanceSnapshot(DateTimeOffset.Now, 12, 3, 8L << 30, 16L << 30, 4, 1024, 2048, 100L << 30, 512L << 30)
            {
                Hardware = new WidgetHardwareNames(
                    "12th Gen Intel Core i7-12700H", "NVIDIA GeForce RTX 4060 Laptop GPU",
                    "DDR4 3200MHz 32 GB", "NVMe WD PC SN740 SDDP")
            }
        };

        using var menu = DesktopBoxForm.BuildMonitorContextMenu(widget);

        var parent = Assert.Single(menu.Items.OfType<ToolStripMenuItem>());
        var rows = parent.DropDownItems.OfType<ToolStripMenuItem>()
            .Select(item => item.Text).ToArray();
        Assert.Contains("处理器：12th Gen Intel Core i7-12700H", rows);
        Assert.Contains("显卡：NVIDIA GeForce RTX 4060 Laptop GPU", rows);
        Assert.Contains("内存：DDR4 3200MHz 32 GB", rows);
        Assert.Contains("磁盘：NVMe WD PC SN740 SDDP", rows);
        // The readings still follow, separated from the names.
        Assert.Contains(rows, row => row.StartsWith("物理内存"));
        Assert.Contains(parent.DropDownItems.Cast<ToolStripItem>(), item => item is ToolStripSeparator);
    }

// The readings used to sit directly in the top level, and their combined
    // height stretched the menu far past the actions the user picks from.
    [Fact]
    public void PerformanceMenuKeepsHardwareReadingsInAChildMenu()
    {
        var widget = new DesktopWidget
        {
            Kind = DesktopWidgetKind.Performance,
            Performance = new WidgetPerformanceSnapshot(DateTimeOffset.Now, 12, 3, 8L << 30, 16L << 30, 4, 1024, 2048, 100L << 30, 512L << 30)
        };

        using var menu = DesktopBoxForm.BuildMonitorContextMenu(widget);

        var parent = Assert.Single(menu.Items.OfType<ToolStripMenuItem>());
        Assert.Equal("硬件信息", parent.Text);
        Assert.Empty(menu.Items.OfType<ToolStripItem>().Where(item => !item.Equals(parent) && item is not ToolStripSeparator));
        Assert.All(parent.DropDownItems.OfType<ToolStripMenuItem>(), item => Assert.False(item.Enabled));
        Assert.Contains(parent.DropDownItems.OfType<ToolStripMenuItem>(), item => item.Text.StartsWith("物理内存"));
        Assert.Contains(parent.DropDownItems.OfType<ToolStripMenuItem>(), item => item.Text.StartsWith("磁盘读"));
        Assert.Contains(parent.DropDownItems.OfType<ToolStripMenuItem>(), item => item.Text.StartsWith("固定磁盘容量"));
    }

    // A disabled parent cannot open its child, so the readings would be
    // unreachable if the container inherited their read-only state.
    [Fact]
    public void TheHardwareReadingParentStaysEnabledSoItsChildCanOpen()
    {
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Performance };

        using var menu = DesktopBoxForm.BuildMonitorContextMenu(widget);

        var parent = Assert.Single(menu.Items.OfType<ToolStripMenuItem>());
        Assert.True(parent.Enabled);
        Assert.Equal(3, parent.DropDownItems.Count);
        // Read-only values: the rows exist but cannot be invoked.
        Assert.Contains(parent.DropDownItems.OfType<ToolStripMenuItem>(), item => item.Text.Contains("等待采样"));
    }

    [Fact]
    public void PerformanceMenuSpendsNoExtraTopLevelRowsOnReadings()
    {
        var withData = new DesktopWidget
        {
            Kind = DesktopWidgetKind.Performance,
            Performance = new WidgetPerformanceSnapshot(DateTimeOffset.Now, 12, 3, 8L << 30, 16L << 30, 4, 1024, 2048, 100L << 30, 512L << 30)
        };
        var withoutData = new DesktopWidget { Kind = DesktopWidgetKind.Performance };

        using var populated = DesktopBoxForm.BuildMonitorContextMenu(withData);
        using var empty = DesktopBoxForm.BuildMonitorContextMenu(withoutData);

        // Same row count either way: the readings never drive the top level.
        Assert.Equal(empty.Items.Count, populated.Items.Count);
        Assert.Single(populated.Items.OfType<ToolStripMenuItem>());
    }

    private static CrabDeskRuntime Fixture()
    {
        var runtime = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
        Set(runtime, "<State>k__BackingField", new CrabDeskState());
        Set(runtime, "<Monitors>k__BackingField", new[] { new MonitorLayout { Id = "test", DeviceName = "test", IsPrimary = true,
            Bounds = new(0, 0, 1000, 700), WorkArea = new(0, 0, 1000, 700),
            PixelBounds = new(0, 0, 1000, 700), PixelWorkArea = new(0, 0, 1000, 700) } });
        Set(runtime, "_beginInvoke", (Action<Action>)(action => action()));
        Set(runtime, "_widgetRequests", new Dictionary<Guid, CancellationTokenSource>());
        Set(runtime, "_weatherRetryAt", new Dictionary<Guid, DateTimeOffset>());
        // Unit tests never write the host machine's volume, brightness or radios.
        Set(runtime, "_controlMonitor", new Mock<IWidgetControlCenterMonitor>().Object);
        return runtime;
    }

    private static void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

    /// <summary>
    /// Runs a body on an STA thread. Constructing a DesktopBoxForm touches WPF's
    /// InputManager, which refuses to initialise on an MTA test thread.
    /// </summary>
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { failure = exception; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }
}
