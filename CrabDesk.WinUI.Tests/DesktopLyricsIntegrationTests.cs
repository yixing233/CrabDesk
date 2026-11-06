using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using CrabDesk.Core;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopLyricsIntegrationTests
{
    [Fact]
    public void LocalLyricsApplyOnlyToTheirAssociatedSong()
    {
        var (runtime, widget) = Fixture();
        var media = new ControlCenterMedia("Song A", "Artist", "Album", false, false, false, false);
        widget.LyricsLocalTrackKey = WidgetLyricsSettings.TrackKey(media);
        widget.LyricsLocalFormat = "lrc";
        widget.LyricsLocalText = "[00:00.00]Local line\n[00:05.00]Next line";
        var remote = new WidgetLyricsDocument([new("Remote line", 0, 5000)], "Remote");
        var matched = (WidgetLyricsSnapshot)Call(runtime, "LyricsForWidget", widget, new WidgetLyricsSnapshot(media, remote))!;
        Assert.Equal("本地歌词", matched.Document!.Source);
        Assert.Equal("Local line", matched.Document.Lines[0].Text);
        var different = new WidgetLyricsSnapshot(media with { Title = "Song B" }, remote);
        Assert.Same(different, Call(runtime, "LyricsForWidget", widget, different));
        Assert.Same(WidgetLyricsSnapshot.Empty, Call(runtime, "LyricsForWidget", widget, WidgetLyricsSnapshot.Empty));
    }

    [Fact]
    public void OptionsNormalizeAndOnlyNotifyWhenValuesChange()
    {
        var (runtime, widget) = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        var changes = 0; runtime.Changed += (_, _) => changes++;
        runtime.SetLyricsOptions(widget, 99, 100, int.MaxValue, false);
        runtime.SetLyricsOptions(widget, 99, 100, int.MaxValue, false);
        Assert.Equal(1, changes); Assert.Equal(9, widget.LyricsLineCount);
        Assert.Equal(48, widget.LyricsFontSize); Assert.Equal(10000, widget.LyricsOffsetMilliseconds);
        runtime.SetLyricsLightText(widget, true); runtime.SetLyricsLightText(widget, true);
        Assert.Equal(2, changes); Assert.True(widget.LyricsLightText);
        runtime.SetLyricsTransparentBackground(widget, true); runtime.SetLyricsTransparentBackground(widget, true);
        Assert.Equal(3, changes); Assert.True(widget.LyricsTransparentBackground);
        runtime.SetLyricsTransparentBackground(widget, false);
        Assert.Equal(4, changes); Assert.False(widget.LyricsTransparentBackground);
        runtime.SetLyricsTypography(widget, " Arial ", 700, WidgetLyricsFontStyle.Italic);
        runtime.SetLyricsTypography(widget, "Arial", 700, WidgetLyricsFontStyle.Italic);
        Assert.Equal(5, changes); Assert.Equal("Arial", widget.LyricsFontFamily);
        Assert.Equal(700, widget.LyricsFontWeight); Assert.Equal(WidgetLyricsFontStyle.Italic, widget.LyricsFontStyle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransparentLyricsRemoveTheFillAndBorderWhileKeepingText(bool acrylic) => OnSta(() =>
    {
        var (runtime, widget) = Fixture();
        SetDisplayLyrics(widget);
        runtime.State.Settings.Appearance.ShowBorder = true;
        runtime.State.Settings.Appearance.BorderWidth = 4;
        using var form = new DesktopBoxForm(runtime, runtime.Monitors[0]);
        form.SetAcrylicBackground(acrylic);
        using var before = Paint(form, acrylic);
        Assert.True(before.GetPixel(2, 145).A > 0);
        widget.LyricsTransparentBackground = true;
        using var transparent = Paint(form, acrylic);
        Assert.Equal(0, transparent.GetPixel(2, 145).A);
        Assert.Equal(0, transparent.GetPixel(185, 291).A);
        var visible = 0;
        for (var y = 0; y < transparent.Height; y++)
            for (var x = 0; x < transparent.Width; x++)
                if (transparent.GetPixel(x, y).A > 0) visible++;
        Assert.True(visible > 100, "The lyrics and player heading must remain visible.");
        Assert.Empty(form.GetAcrylicRegions());
        widget.LyricsTransparentBackground = false;
        using var restored = Paint(form, acrylic);
        Assert.Equal(before.GetPixel(2, 145), restored.GetPixel(2, 145));
        Assert.Single(form.GetAcrylicRegions());
        var directory = Environment.GetEnvironmentVariable("CRABDESK_LYRICS_VISUAL_OUTPUT");
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            transparent.Save(Path.Combine(directory, $"lyrics-transparent-{(acrylic ? "acrylic" : "regular")}.png"));
        }
    });

    [Fact]
    public void TransparentLyricsDoNotBlurTheContentUnderneath() => OnSta(() =>
    {
        var (runtime, widget) = Fixture();
        SetDisplayLyrics(widget);
        widget.LyricsTransparentBackground = true;
        runtime.State.Widgets.Remove(widget);
        runtime.State.Widgets.Add(new DesktopWidget { Kind = DesktopWidgetKind.Note, MonitorId = widget.MonitorId,
            Bounds = widget.Bounds, Text = "底下的文字保持清晰\n这行内容不应该被透明歌词模糊\n继续显示桌面内容" });
        using var form = new DesktopBoxForm(runtime, runtime.Monitors[0]);
        form.SetAcrylicBackground(true);
        using var before = Paint(form, true);
        runtime.State.Widgets.Add(widget);
        using var after = Paint(form, true);
        var distinct = new HashSet<int>();
        // This band contains the underlying note's text, above the lyric line.
        for (var y = 50; y < 85; y++)
            for (var x = 16; x < 340; x++)
            {
                var expected = before.GetPixel(x, y);
                distinct.Add(expected.ToArgb());
                Assert.Equal(expected, after.GetPixel(x, y));
            }
        Assert.True(distinct.Count > 10, "The comparison must include text edges, not just a uniform background.");
        Assert.DoesNotContain(form.GetAcrylicRegions(), region => region.Id == widget.Id);
    });

    [Fact]
    public void TransparentOnlyMonitorKeepsItsForegroundAndInputRegion() => OnSta(() =>
    {
        var (runtime, widget) = Fixture();
        SetDisplayLyrics(widget);
        widget.LyricsTransparentBackground = true;
        using var form = new DesktopBoxForm(runtime, runtime.Monitors[0]);
        using var parent = new System.Windows.Forms.Form();
        form.AttachToDesktop(parent.Handle);
        form.SetAcrylicBackground(true);
        Bitmap? foreground = null;
        form.SetAcrylicFramePresenter((bitmap, _, regions) => { foreground = bitmap; Assert.Empty(regions); });
        form.SetAcrylicEmptyFramePresenter(() => Assert.Fail("Transparent lyrics still require a foreground."));
        Assert.True(form.RefreshWorkspace(), form.LayerDiagnostic);
        Assert.NotNull(foreground);
        Assert.Equal(0, foreground.GetPixel(2, 145).A);
        Assert.True(form.ValidateWindowRegion(), "Transparency must preserve the widget's native input region.");
        Assert.NotNull(Call(form, "WidgetAt", new PointF(60, 145)));
    });

    private static void SetDisplayLyrics(DesktopWidget widget) => widget.Lyrics = new(
        new ControlCenterMedia("Song", "Artist", "", false, false, false, false)
            { Position = TimeSpan.FromSeconds(2), Duration = TimeSpan.FromSeconds(10) },
        new([new("歌词仍然清晰显示", 0, 10000)], "Fixture"));

    [Fact]
    public void DesktopAndGalleryReceiveTheSelectedTypography() => OnSta(() =>
    {
        var (runtime, widget) = Fixture();
        SetDisplayLyrics(widget);
        widget.Lyrics = widget.Lyrics! with { Document = new([new("Wide iii MMM typography", 0, 10000)], "Typography") };
        widget.LyricsTransparentBackground = true;
        widget.LyricsFontFamily = "Arial"; widget.LyricsFontWeight = 400;
        using var form = new DesktopBoxForm(runtime, runtime.Monitors[0]);
        using var before = Paint(form, false);
        var previewBefore = DesktopLyricsPreview.Render(widget, Color.White, Color.CornflowerBlue, 356, 248);
        widget.LyricsFontFamily = "Consolas"; widget.LyricsFontWeight = 900; widget.LyricsFontStyle = WidgetLyricsFontStyle.Italic;
        using var after = Paint(form, false);
        var previewAfter = DesktopLyricsPreview.Render(widget, Color.White, Color.CornflowerBlue, 356, 248);
        Assert.False(previewBefore.SequenceEqual(previewAfter));
        var changed = 0;
        for (var y = 35; y < before.Height; y++)
            for (var x = 12; x < before.Width - 12; x++)
                if (before.GetPixel(x, y) != after.GetPixel(x, y)) changed++;
        Assert.True(changed > 100);
    });

    private static Bitmap Paint(DesktopBoxForm form, bool acrylic)
    {
        var bitmap = DesktopLayerBitmapFactory.Create(372, 292);
        if (acrylic) Call(form, "PaintAcrylicFrame", bitmap);
        else
        {
            using var graphics = Graphics.FromImage(bitmap);
            Call(form, "DrawWidgets", graphics, new RectangleF(0, 0, bitmap.Width, bitmap.Height));
        }
        return bitmap;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DesktopSurfaceAndGalleryRenderTheLyricsWidget(bool light) => OnSta(() =>
    {
        var (runtime, widget) = Fixture();
        widget.Lyrics = new(new ControlCenterMedia("桌面歌词", "预览", "", false, false, false, false)
            { Position = TimeSpan.FromSeconds(12), Duration = TimeSpan.FromSeconds(25), CapturedAt = DateTimeOffset.Now },
            new([new("前面的旋律", 0, 5000), new("上一句的回声", 5000, 10000),
                new("当前歌词慢慢点亮", 10000, 15000, [new("当前歌词", 10000, 12000), new("慢慢点亮", 12000, 15000)]),
                new("下一句将要响起", 15000, 20000), new("旋律继续向前", 20000, 25000)], "测试歌词"));
        widget.LyricsLightText = !light;
        runtime.State.Settings.DefaultBoxAppearance.Background = light ? "#F2F4F7" : "#22252B";
        using var form = new DesktopBoxForm(runtime, runtime.Monitors[0]);
        using var bitmap = new Bitmap(372, 292);
        using (var graphics = Graphics.FromImage(bitmap)) Call(form, "DrawWidget", graphics, widget);
        var directory = Environment.GetEnvironmentVariable("CRABDESK_LYRICS_VISUAL_OUTPUT") ?? Path.GetTempPath();
        Directory.CreateDirectory(directory);
        bitmap.Save(Path.Combine(directory, $"lyrics-widget-{(light ? "light" : "dark")}.png"));
        var bytes = DesktopLyricsPreview.Render(widget, light ? Color.Black : Color.White, Color.CornflowerBlue, 356, 248);
        using var stream = new MemoryStream(bytes); using var image = Image.FromStream(stream);
        Assert.Equal(356, image.Width); Assert.Equal(248, image.Height);
        Assert.False(form.Visible);
    });

    private static (CrabDeskRuntime Runtime, DesktopWidget Widget) Fixture()
    {
        var runtime = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
        Set(runtime, "<State>k__BackingField", new CrabDeskState());
        Set(runtime, "<Items>k__BackingField", Array.Empty<DesktopItemRef>());
        Set(runtime, "<Monitors>k__BackingField", new[] { new MonitorLayout { Id = "test", DeviceName = "test", IsPrimary = true,
            Bounds = new(0, 0, 1000, 700), WorkArea = new(0, 0, 1000, 700), PixelBounds = new(0, 0, 1000, 700), PixelWorkArea = new(0, 0, 1000, 700) } });
        Set(runtime, "_beginInvoke", (Action<Action>)(_ => { }));
        Set(runtime, "_localLyrics", new Dictionary<Guid, (string, string, WidgetLyricsDocument?)>());
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Lyrics, Size = DesktopWidgetSize.Panel, MonitorId = "test", Bounds = new(0, 0, 372, 292) };
        runtime.State.Widgets.Add(widget);
        return (runtime, widget);
    }

    private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
