using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using CrabDesk.Core;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class WidgetContentRenderingTests
{
    [Theory]
    [InlineData(false, 1d)]
    [InlineData(true, 1d)]
    [InlineData(false, 1.25d)]
    [InlineData(true, 1.25d)]
    public void CompactNetworkUsesOneSurfaceWithSeparatedSpeedRows(bool dark, double scale) => OnSta(() =>
    {
        var monitor = new MonitorLayout { Id = "compact-network", DeviceName = "compact-network", IsPrimary = true, DpiScale = scale,
            Bounds = new(0, 0, 1000, 700), WorkArea = new(0, 0, 1000, 700),
            PixelBounds = new(0, 0, 1000 * scale, 700 * scale), PixelWorkArea = new(0, 0, 1000 * scale, 700 * scale) };
        var runtime = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
        Set(runtime, "<State>k__BackingField", new CrabDeskState());
        Set(runtime, "<Items>k__BackingField", Array.Empty<DesktopItemRef>());
        Set(runtime, "<Monitors>k__BackingField", new[] { monitor });
        var background = dark ? Color.FromArgb(28, 37, 53) : Color.FromArgb(247, 249, 252);
        runtime.State.Settings.DefaultBoxAppearance.Background = ColorTranslator.ToHtml(background);
        runtime.State.Settings.DefaultBoxAppearance.BackgroundOpacity = 1;
        runtime.State.Settings.Appearance.CornerRadius = 20;
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Network, MonitorId = monitor.Id, Size = DesktopWidgetSize.Small,
            NetworkDisplayMode = NetworkWidgetDisplayMode.Speeds, Bounds = new(20, 20, 180, 84),
            Network = new(DateTimeOffset.Now) { IsConnected = true, DownloadBytesPerSecond = 70.7 * 1024, UploadBytesPerSecond = 813.5 * 1024 } };
        runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor);
        using var image = new Bitmap((int)(220 * scale), (int)(124 * scale));
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.Gray); graphics.ScaleTransform((float)scale, (float)scale);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            typeof(DesktopBoxForm).GetMethod("DrawWidget", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(surface, [graphics, widget]);
        }
        if (Environment.GetEnvironmentVariable("CRABDESK_WIDGET_CONTENT_OUTPUT") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            image.Save(Path.Combine(output, $"compact-network-{(dark ? "dark" : "light")}-{scale}.png"));
        }
        var rows = WidgetNetworkGraph.SpeedLayout(widget.Bounds.Width, widget.Bounds.Height, runtime.State.Settings.Appearance.CornerRadius);
        Assert.True(rows.Upload.Y - rows.Download.Y - rows.Download.Height >= 6);
        foreach (var row in new[] { rows.Download, rows.Upload })
        {
            Assert.Equal(background.ToArgb(), image.GetPixel((int)Math.Round((20 + row.X + row.Width - 4) * scale),
                (int)Math.Round((20 + row.Y + 2) * scale)).ToArgb());
            var firstInk = image.Width; var lastInk = 0;
            for (var y = (int)((20 + row.Y) * scale); y < (20 + row.Y + row.Height) * scale; y++)
            for (var x = (int)((20 + row.X - 1) * scale); x < 197 * scale; x++)
            {
                var pixel = image.GetPixel(x, y);
                if (Math.Abs(pixel.R - background.R) + Math.Abs(pixel.G - background.G) + Math.Abs(pixel.B - background.B) < 90) continue;
                firstInk = Math.Min(firstInk, x); lastInk = Math.Max(lastInk, x);
            }
            Assert.True(lastInk > firstInk);
            var leftPadding = firstInk - 20 * scale;
            var rightPadding = 200 * scale - lastInk - 1;
            Assert.True(Math.Abs(leftPadding - rightPadding) <= 4 * scale,
                $"Unbalanced padding: left={leftPadding}, right={rightPadding}, scale={scale}.");
        }
    });

    [Theory]
    [InlineData(false, 1d)]
    [InlineData(true, 1d)]
    [InlineData(false, 1.5d)]
    [InlineData(true, 1.5d)]
    [InlineData(false, 2d)]
    [InlineData(true, 2d)]
    public void ContentRendersInsideCompactAndWideCardsAtCommonDpi(bool dark, double scale) => OnSta(() =>
    {
        var monitor = new MonitorLayout { Id = "content-render", DeviceName = "content-render", IsPrimary = true, DpiScale = scale,
            Bounds = new(0, 0, 1000, 700), WorkArea = new(0, 0, 1000, 700),
            PixelBounds = new(0, 0, 1000 * scale, 700 * scale), PixelWorkArea = new(0, 0, 1000 * scale, 700 * scale) };
        var runtime = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
        Set(runtime, "<State>k__BackingField", new CrabDeskState());
        Set(runtime, "<Items>k__BackingField", Array.Empty<DesktopItemRef>());
        Set(runtime, "<Monitors>k__BackingField", new[] { monitor });
        runtime.State.Settings.Appearance.AnimationEnabled = false;
        runtime.State.Settings.DefaultBoxAppearance.Background = dark ? "#28313D" : "#F7F9FC";
        runtime.State.Settings.DefaultBoxAppearance.BackgroundOpacity = 1;
        using var surface = new DesktopBoxForm(runtime, monitor);
        var now = DateTimeOffset.Now;
        foreach (var width in new[] { 180, 372 })
        foreach (var kind in Enum.GetValues<DesktopWidgetKind>())
        {
            var height = kind is DesktopWidgetKind.ControlCenter or DesktopWidgetKind.Lyrics ? 292 : 180;
            var widget = new DesktopWidget { Kind = kind, MonitorId = monitor.Id, Size = DesktopWidgetSize.Custom,
                Bounds = new(20, 20, width, height), Text = "今天的灵感\n整理手头的工作，留一点时间给自己。", Translation = "Make room for a new idea.",
                Tasks = [new() { Text = "整理今天的计划", EndDate = DateOnly.FromDateTime(DateTime.Today) }, new() { Text = "完成第一项任务", IsCompleted = true }],
                Performance = new(now, 24, 12, 9_000_000_000, 16_000_000_000, 8, 400_000, 120_000, 100_000, 500_000),
                Network = new(now) { IsConnected = true, NetworkType = "以太网", DownloadBytesPerSecond = 2_400_000, UploadBytesPerSecond = 384_000,
                    LatencyMilliseconds = 18, Processes = new(NetworkProcessStatus.Ready,
                        [new(100, "msedge", 1_600_000, 28000), new(200, "OneDrive", 640_000, 320_000), new(300, "steam", 180_000, 36000)]) },
                Controls = new() { WiFiEnabled = true, BluetoothEnabled = true, NightLightEnabled = false, VolumePercent = 42, VolumeMuted = false,
                    BrightnessPercent = 65, OutputDeviceName = "耳机 · 音频设备", Media = new("让旋律慢慢靠近", "CrabDesk", "", false, true, true, true) },
                Location = new("北京", "北京", "中国", 39.9, 116.4),
                Weather = new(27, 24, 29, 2, now) { FeelsLike = 28, Humidity = 52, WindSpeed = 8, AirQuality = 42,
                    Hourly = Enumerable.Range(1, 6).Select(index => new WidgetHourlyWeather(now.AddHours(index),
                        28 - index, index < 4 ? "小雨" : "多云", null)).ToArray() } };
            runtime.State.Widgets.Clear(); runtime.State.Widgets.Add(widget);
            using var image = new Bitmap((int)Math.Ceiling((width + 40) * scale), (int)Math.Ceiling((height + 40) * scale));
            using (var graphics = Graphics.FromImage(image))
            {
                graphics.Clear(Color.FromArgb(89, 100, 116));
                graphics.ScaleTransform((float)scale, (float)scale);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                typeof(DesktopBoxForm).GetMethod("DrawWidget", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(surface, [graphics, widget]);
            }
            Assert.Equal(Color.FromArgb(89, 100, 116), image.GetPixel(0, 0));
            Assert.Equal(Color.FromArgb(89, 100, 116), image.GetPixel(image.Width - 1, image.Height - 1));
            if (kind == DesktopWidgetKind.Performance)
            {
                var body = WidgetMonitorPresentation.Body(width, height, runtime.State.Settings.Appearance.CornerRadius);
                for (var index = 0; index < 4; index++)
                {
                    var cell = WidgetMonitorPresentation.PerformanceCell(body, index);
                    var colors = 0;
                    for (var x = (int)((20 + cell.X) * scale); x < (20 + cell.X + 20) * scale; x++)
                    for (var y = (int)((20 + cell.Y) * scale); y < (20 + cell.Y + 20) * scale; y++)
                    {
                        var pixel = image.GetPixel(x, y);
                        if (Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) - Math.Min(pixel.R, Math.Min(pixel.G, pixel.B)) > 35) colors++;
                    }
                    Assert.True(colors > 20, $"Hardware {index} lost its filled color at {scale} DPI.");
                }
            }
            if (scale == 1 && Environment.GetEnvironmentVariable("CRABDESK_WIDGET_CONTENT_OUTPUT") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output);
                image.Save(Path.Combine(output, $"{(dark ? "dark" : "light")}-{WidgetIconCatalog.GetName(kind)}-{width}.png"));
            }
        }
    });

    private static void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { failure = exception; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
