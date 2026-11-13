using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using CrabDesk.Core;
using CrabDesk.Runtime;
using Xunit;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopLyricsIntegrationTests
{
    [Fact]
    public void ReportLyricsMenuConstructionAndOpeningCost() => OnSta(() =>
    {
        var (runtime, widget) = Fixture();
        using var font = new Font("Segoe UI", 9);
        Set(runtime, "_menuFont", font);
        Set(runtime, "_lightTrayRenderer", new FluentMenuRenderer(false));
        Set(runtime, "_darkTrayRenderer", new FluentMenuRenderer(true));
        Set(runtime, "_configuredSubmenus", new ConditionalWeakTable<System.Windows.Forms.ToolStripDropDown, object>());
        using var form = new DesktopBoxForm(runtime, runtime.Monitors[0]);
        var results = new List<object>();
        for (var pass = 0; pass < 6; pass++)
        {
            using var menu = new FluentContextMenuStrip { PreferredDpiScale = 1, OpacityAnimationAllowed = false,
                ConfigureSubmenusOnOpening = true };
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Call(form, "AddLyricsContextMenu", menu, widget);
            var build = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            runtime.ApplyContextMenuTheme(menu);
            var theme = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            menu.Show(new Point(-30000, -30000));
            var opening = watch.Elapsed.TotalMilliseconds;
            results.Add(new { Pass = pass, BuildMilliseconds = build, ThemeMilliseconds = theme, OpeningMilliseconds = opening });
            menu.Close();
        }
        var output = Environment.GetEnvironmentVariable("CRABDESK_LYRICS_VISUAL_OUTPUT") ?? Path.GetTempPath();
        Directory.CreateDirectory(output);
        var label = Environment.GetEnvironmentVariable("CRABDESK_PERF_LABEL") ?? "current";
        File.WriteAllText(Path.Combine(output, $"lyrics-menu-cost-{label}.json"), System.Text.Json.JsonSerializer.Serialize(results));
    });

    [Theory]
    [InlineData(1d, false)]
    [InlineData(1.25d, false)]
    [InlineData(1.5d, false)]
    [InlineData(1d, true)]
    [InlineData(1.25d, true)]
    [InlineData(1.5d, true)]
    public void CalibrationSliderUpdatesHundredthsAndKeepsItsSizeInThemedMenus(double scale, bool dark) => OnSta(() =>
    {
        var (runtime, widget) = Fixture(scale);
        widget.LyricsOffsetMilliseconds = 750;
        using var saveTimer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        using var font = new Font("Segoe UI", 9);
        Set(runtime, "_saveTimer", saveTimer);
        Set(runtime, "_menuFont", font);
        Set(runtime, "_lightTrayRenderer", new FluentMenuRenderer(false));
        Set(runtime, "_darkTrayRenderer", new FluentMenuRenderer(true));
        Set(runtime, "<IsDarkTheme>k__BackingField", dark);
        Set(runtime, "_configuredSubmenus", new ConditionalWeakTable<System.Windows.Forms.ToolStripDropDown, object>());
        using var form = new DesktopBoxForm(runtime, runtime.Monitors[0]);
        using var menu = new FluentContextMenuStrip { PreferredDpiScale = (float)scale, ConfigureSubmenusOnOpening = true };
        var timing = (FluentToolStripMenuItem)Call(form, "BuildLyricsTimingMenu", widget)!;
        menu.Items.Add(timing);
        runtime.ApplyContextMenuTheme(menu);
        Assert.IsType<System.Windows.Forms.Panel>(((System.Windows.Forms.ToolStripControlHost)timing.DropDownItems[0]).Control);
        OpenSliderMenu(timing);
        var host = Assert.IsType<System.Windows.Forms.ToolStripControlHost>(timing.DropDownItems[0]);
        var card = Assert.IsType<LyricsSliderCard>(host.Control);
        var slider = card.Slider;
        var label = card.ValueText;
        Assert.Equal((int)Math.Round(LyricsSliderCard.CardHeight * scale), host.Height);
        var expectedWidth = (int)Math.Round(LyricsSliderCard.CardWidth * scale);
        Assert.Equal(expectedWidth, host.Width);
        var popupSize = timing.DropDown.Size;
        for (var pass = 0; pass < 5; pass++)
        {
            runtime.ApplyContextMenuTheme(menu);
            OpenSliderMenu(timing);
            typeof(CrabDeskRuntime).GetMethod("OnSubmenuOpened", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { timing.DropDown, EventArgs.Empty });
            timing.DropDown.PerformLayout();
            Assert.Equal(popupSize, timing.DropDown.Size);
            Assert.Equal(expectedWidth, card.Width);
            Assert.Equal(expectedWidth, host.Width);
            Assert.Same(card, host.Control);
        }
        Assert.Equal(menu.BackColor, host.Control.BackColor);
        Assert.Equal(75, slider.Value);
        Assert.Equal(-1000, slider.Minimum);
        Assert.Equal(1000, slider.Maximum);
        Assert.Equal(1, slider.SmallChange);
        card.IncreaseButton.RaiseEvent(new Wpf.RoutedEventArgs(WpfControls.Button.ClickEvent));
        Assert.Equal(760, widget.LyricsOffsetMilliseconds);
        Assert.Contains("+0.76", label.Text);
        card.DecreaseButton.RaiseEvent(new Wpf.RoutedEventArgs(WpfControls.Button.ClickEvent));
        Assert.Equal(750, widget.LyricsOffsetMilliseconds);
        slider.Value = slider.Maximum - slider.SmallChange;
        card.IncreaseButton.RaiseEvent(new Wpf.RoutedEventArgs(WpfControls.Button.ClickEvent));
        Assert.Equal(10000, widget.LyricsOffsetMilliseconds);
        Assert.False(card.IncreaseButton.IsEnabled);
        card.IncreaseButton.RaiseEvent(new Wpf.RoutedEventArgs(WpfControls.Button.ClickEvent));
        Assert.Equal(10000, widget.LyricsOffsetMilliseconds);
        slider.Value = slider.Minimum + slider.SmallChange;
        card.DecreaseButton.RaiseEvent(new Wpf.RoutedEventArgs(WpfControls.Button.ClickEvent));
        Assert.Equal(-10000, widget.LyricsOffsetMilliseconds);
        Assert.False(card.DecreaseButton.IsEnabled);
        card.DecreaseButton.RaiseEvent(new Wpf.RoutedEventArgs(WpfControls.Button.ClickEvent));
        Assert.Equal(-10000, widget.LyricsOffsetMilliseconds);
        foreach (var value in new[] { -1000, -1, 0, 1, 123, 1000 })
        {
            slider.Value = value;
            Assert.Equal(value * 10, widget.LyricsOffsetMilliseconds);
            Assert.Equal(1.75, widget.LyricsLineSpacing);
        }
        slider.Value = 123;
        Assert.Contains("+1.23", label.Text);
        card.ResetButton.RaiseEvent(new Wpf.RoutedEventArgs(WpfControls.Button.ClickEvent));
        Assert.Equal(0, slider.Value);
        Assert.Equal(0, widget.LyricsOffsetMilliseconds);
        Assert.Contains("0.00", label.Text);
        Assert.True(card.DecreaseButton.IsEnabled);
        Assert.True(card.IncreaseButton.IsEnabled);
        ExportSliderCard(card, scale, $"lyrics-calibration-{(dark ? "dark" : "light")}-{scale * 100:0}");
    });

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
        Assert.Equal(1, changes); Assert.Equal(3, widget.LyricsLineSpacing);
        Assert.Equal(48, widget.LyricsFontSize); Assert.Equal(10000, widget.LyricsOffsetMilliseconds);
        runtime.SetLyricsLightText(widget, true); runtime.SetLyricsLightText(widget, true);
        Assert.Equal(2, changes); Assert.True(widget.LyricsLightText);
        runtime.SetLyricsTransparentBackground(widget, true); runtime.SetLyricsTransparentBackground(widget, true);
        Assert.Equal(3, changes); Assert.True(widget.LyricsTransparentBackground);
        Assert.Equal(WidgetBackgroundMaterial.Acrylic, widget.BackgroundMaterial);
        runtime.SetLyricsTransparentBackground(widget, false);
        Assert.Equal(4, changes); Assert.False(widget.LyricsTransparentBackground);
        Assert.Equal(WidgetBackgroundMaterial.Acrylic, widget.BackgroundMaterial);
        runtime.SetLyricsTypography(widget, " Arial ", 700, WidgetLyricsFontStyle.Italic);
        runtime.SetLyricsTypography(widget, "Arial", 700, WidgetLyricsFontStyle.Italic);
        Assert.Equal(5, changes); Assert.Equal("Arial", widget.LyricsFontFamily);
        Assert.Equal(700, widget.LyricsFontWeight); Assert.Equal(WidgetLyricsFontStyle.Italic, widget.LyricsFontStyle);
    }

    [Fact]
    public void SelectingAcrylicRestoresTheLyricsMaterialAndOnlyNotifiesOnce()
    {
        var (runtime, widget) = Fixture();
        runtime.State.Settings.Appearance.WidgetAcrylic.FollowBoxAppearance = false;
        widget.BackgroundMaterial = WidgetBackgroundMaterial.Solid;
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        var changes = 0;
        runtime.Changed += (_, _) => changes++;
        runtime.SetLyricsTransparentBackground(widget, false);
        runtime.SetLyricsTransparentBackground(widget, false);
        Assert.Equal(1, changes);
        Assert.Equal(WidgetBackgroundMaterial.Acrylic, widget.BackgroundMaterial);
        Assert.False(widget.LyricsTransparentBackground);
        Assert.True(WidgetAppearance.RequiresAcrylic(runtime.State));
    }

    [Theory]
    [InlineData(1d)]
    [InlineData(1.25d)]
    [InlineData(1.5d)]
    public void LineSpacingSliderUpdatesTheLayoutAndRestoresItsDefault(double scale) => OnSta(() =>
    {
        var (runtime, widget) = Fixture(scale);
        using var saveTimer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", saveTimer);
        using var form = new DesktopBoxForm(runtime, runtime.Monitors[0]);
        using var menu = (FluentToolStripMenuItem)Call(form, "BuildLyricsSpacingMenu", widget)!;
        OpenSliderMenu(menu);
        var host = Assert.IsType<System.Windows.Forms.ToolStripControlHost>(menu.DropDownItems[0]);
        var card = Assert.IsType<LyricsSliderCard>(host.Control);
        var slider = card.Slider;
        Assert.Equal(100, slider.Minimum);
        Assert.Equal(300, slider.Maximum);
        Assert.Equal(5, slider.SmallChange);
        Assert.Equal(175, slider.Value);
        card.IncreaseButton.RaiseEvent(new Wpf.RoutedEventArgs(WpfControls.Button.ClickEvent));
        Assert.Equal(1.80, widget.LyricsLineSpacing);
        card.DecreaseButton.RaiseEvent(new Wpf.RoutedEventArgs(WpfControls.Button.ClickEvent));
        Assert.Equal(1.75, widget.LyricsLineSpacing);
        slider.Value = slider.Minimum;
        card.DecreaseButton.RaiseEvent(new Wpf.RoutedEventArgs(WpfControls.Button.ClickEvent));
        Assert.Equal(1, widget.LyricsLineSpacing);
        Assert.False(card.DecreaseButton.IsEnabled);
        slider.Value = slider.Maximum;
        card.IncreaseButton.RaiseEvent(new Wpf.RoutedEventArgs(WpfControls.Button.ClickEvent));
        Assert.Equal(3, widget.LyricsLineSpacing);
        Assert.False(card.IncreaseButton.IsEnabled);
        slider.Value = 225;
        Assert.Equal(2.25, widget.LyricsLineSpacing);
        Assert.Contains("2.25", menu.Text);
        Assert.Equal(0, widget.LyricsOffsetMilliseconds);
        Assert.Equal(24, widget.LyricsFontSize);
        slider.Value = 227;
        Assert.Equal(225, slider.Value);
        Assert.Equal(2.25, widget.LyricsLineSpacing);
        card.ResetButton.RaiseEvent(new Wpf.RoutedEventArgs(WpfControls.Button.ClickEvent));
        Assert.Equal(1.75, widget.LyricsLineSpacing);
        ExportSliderCard(card, scale, $"lyrics-spacing-light-{scale * 100:0}");
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SliderDraggingCoalescesUpdatesAndFlushesTheFinalValue(bool spacing) => OnSta(() =>
    {
        var (runtime, widget) = Fixture();
        using var saveTimer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", saveTimer);
        using var form = new DesktopBoxForm(runtime, runtime.Monitors[0]);
        using var menu = (FluentToolStripMenuItem)Call(form, spacing ? "BuildLyricsSpacingMenu" : "BuildLyricsTimingMenu", widget)!;
        OpenSliderMenu(menu);
        var card = (LyricsSliderCard)((System.Windows.Forms.ToolStripControlHost)menu.DropDownItems[0]).Control;
        var changes = 0;
        runtime.Changed += (_, _) => changes++;
        card.Slider.RaiseEvent(new WpfControls.Primitives.DragStartedEventArgs(0, 0)
            { RoutedEvent = WpfControls.Primitives.Thumb.DragStartedEvent });
        foreach (var value in spacing ? new[] { 200, 205, 210, 225 } : new[] { 1, 25, 75, 123 }) card.Slider.Value = value;
        Assert.Equal(0, changes);
        Assert.Contains(spacing ? "2.25" : "+1.23", card.ValueText.Text);
        Assert.Equal(spacing ? 1.75 : 0, spacing ? widget.LyricsLineSpacing : widget.LyricsOffsetMilliseconds);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (changes == 0 && watch.ElapsedMilliseconds < 1000)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Thread.Sleep(5);
        }
        Assert.Equal(1, changes);
        Assert.Equal(spacing ? 2.25 : 1230, spacing ? widget.LyricsLineSpacing : widget.LyricsOffsetMilliseconds);
        card.Slider.Value = spacing ? 230 : 124;
        Assert.Equal(1, changes);
        card.Slider.RaiseEvent(new WpfControls.Primitives.DragCompletedEventArgs(0, 0, false)
            { RoutedEvent = WpfControls.Primitives.Thumb.DragCompletedEvent });
        Assert.Equal(2, changes);
        Assert.Equal(spacing ? 2.30 : 1240, spacing ? widget.LyricsLineSpacing : widget.LyricsOffsetMilliseconds);
        card.IncreaseButton.RaiseEvent(new Wpf.RoutedEventArgs(WpfControls.Button.ClickEvent));
        Assert.Equal(3, changes);
        Assert.Equal(spacing ? 2.35 : 1250, spacing ? widget.LyricsLineSpacing : widget.LyricsOffsetMilliseconds);
        card.Slider.RaiseEvent(new WpfControls.Primitives.DragStartedEventArgs(0, 0)
            { RoutedEvent = WpfControls.Primitives.Thumb.DragStartedEvent });
        card.Slider.Value = spacing ? 240 : 126;
        card.Dispose();
        Assert.Equal(4, changes);
        Assert.Equal(spacing ? 2.40 : 1260, spacing ? widget.LyricsLineSpacing : widget.LyricsOffsetMilliseconds);
    });

    [Fact]
    public void LyricsSlidersAreCreatedOnlyWhenTheirOwnSubmenuOpens() => OnSta(() =>
    {
        var (runtime, widget) = Fixture();
        using var form = new DesktopBoxForm(runtime, runtime.Monitors[0]);
        using var timing = (FluentToolStripMenuItem)Call(form, "BuildLyricsTimingMenu", widget)!;
        using var spacing = (FluentToolStripMenuItem)Call(form, "BuildLyricsSpacingMenu", widget)!;
        Assert.IsType<System.Windows.Forms.Panel>(((System.Windows.Forms.ToolStripControlHost)timing.DropDownItems[0]).Control);
        Assert.IsType<System.Windows.Forms.Panel>(((System.Windows.Forms.ToolStripControlHost)spacing.DropDownItems[0]).Control);
        widget.LyricsOffsetMilliseconds = 1230;
        widget.LyricsLineSpacing = 2.25;
        OpenSliderMenu(timing);
        var card = (LyricsSliderCard)((System.Windows.Forms.ToolStripControlHost)timing.DropDownItems[0]).Control;
        Assert.Equal(123, card.Slider.Value);
        Assert.IsType<System.Windows.Forms.Panel>(((System.Windows.Forms.ToolStripControlHost)spacing.DropDownItems[0]).Control);
        OpenSliderMenu(timing);
        Assert.Same(card, ((System.Windows.Forms.ToolStripControlHost)timing.DropDownItems[0]).Control);
        OpenSliderMenu(spacing);
        Assert.Equal(225, ((LyricsSliderCard)((System.Windows.Forms.ToolStripControlHost)spacing.DropDownItems[0]).Control).Slider.Value);
    });

    private static void OpenSliderMenu(FluentToolStripMenuItem menu)
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(System.Windows.Forms.ToolStripMenuItem).GetMethod("OnDropDownShow", hidden)!.Invoke(menu, [EventArgs.Empty]);
        menu.DropDown.GetType().GetMethod("OnOpening", hidden)!.Invoke(menu.DropDown, [new System.ComponentModel.CancelEventArgs()]);
    }

    [Theory]
    [InlineData(false, 1d)]
    [InlineData(true, 1d)]
    [InlineData(false, 1.5d)]
    [InlineData(true, 1.5d)]
    public void DeferredLyricsSubmenusKeepTheirThemeAndMetricsAtEveryLevel(bool dark, double scale) => OnSta(() =>
    {
        var (runtime, _) = Fixture(scale);
        using var font = new Font("Segoe UI", 9);
        Set(runtime, "_menuFont", font);
        Set(runtime, "_lightTrayRenderer", new FluentMenuRenderer(false));
        Set(runtime, "_darkTrayRenderer", new FluentMenuRenderer(true));
        Set(runtime, "<IsDarkTheme>k__BackingField", dark);
        var configured = new ConditionalWeakTable<System.Windows.Forms.ToolStripDropDown, object>();
        Set(runtime, "_configuredSubmenus", configured);
        using var menu = new FluentContextMenuStrip { PreferredDpiScale = (float)scale, ConfigureSubmenusOnOpening = true };
        var parent = new FluentToolStripMenuItem("歌词样式");
        var nested = new FluentToolStripMenuItem("字体");
        var option = new FluentToolStripMenuItem("默认字体") { Checked = true };
        nested.DropDownItems.Add(option);
        parent.DropDownItems.Add(nested);
        menu.Items.Add(parent);
        runtime.ApplyContextMenuTheme(menu);
        Assert.True(configured.TryGetValue(parent.DropDown, out _));
        Assert.False(configured.TryGetValue(nested.DropDown, out _));
        OpenSliderMenu(parent);
        Assert.True(configured.TryGetValue(nested.DropDown, out _));
        OpenSliderMenu(nested);
        var popupSize = nested.DropDown.Size;
        for (var pass = 0; pass < 3; pass++)
        {
            runtime.ApplyContextMenuTheme(menu);
            OpenSliderMenu(parent);
            OpenSliderMenu(nested);
            Assert.Equal(popupSize, nested.DropDown.Size);
            Assert.Equal(menu.BackColor, parent.DropDown.BackColor);
            Assert.Equal(menu.BackColor, nested.DropDown.BackColor);
            Assert.Equal(menu.ForeColor, option.ForeColor);
            Assert.Equal(font, option.Font);
            Assert.Equal((int)Math.Round(30 * scale), option.Height);
            Assert.True(option.Checked);
            Assert.False(((System.Windows.Forms.ToolStripDropDownMenu)nested.DropDown).ShowImageMargin);
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenLyricsMenuKeepsItsControlsResponsiveAndFlushesOnClose(bool dark) => OnSta(() =>
    {
        // This test opens native WPF hosts, so use the display's actual DPI.
        var scale = Media.VisualTreeHelper.GetDpi(new WpfControls.Border()).DpiScaleX;
        var (runtime, widget) = Fixture(scale);
        using var saveTimer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        using var font = new Font("Segoe UI", 9);
        Set(runtime, "_saveTimer", saveTimer);
        Set(runtime, "_menuFont", font);
        Set(runtime, "_lightTrayRenderer", new FluentMenuRenderer(false));
        Set(runtime, "_darkTrayRenderer", new FluentMenuRenderer(true));
        Set(runtime, "<IsDarkTheme>k__BackingField", dark);
        Set(runtime, "_configuredSubmenus", new ConditionalWeakTable<System.Windows.Forms.ToolStripDropDown, object>());
        using var form = new DesktopBoxForm(runtime, runtime.Monitors[0]);
        using var menu = new FluentContextMenuStrip { PreferredDpiScale = (float)scale,
            ConfigureSubmenusOnOpening = true, OpacityAnimationAllowed = false };
        var timing = (FluentToolStripMenuItem)Call(form, "BuildLyricsTimingMenu", widget)!;
        menu.Items.Add(timing);
        runtime.ApplyContextMenuTheme(menu);
        var placeholder = ((System.Windows.Forms.ToolStripControlHost)timing.DropDownItems[0]).Control;
        Assert.IsType<System.Windows.Forms.Panel>(placeholder);
        menu.Show(new Point(-30000, -30000));
        timing.ShowDropDown();
        Assert.True(placeholder.IsDisposed);
        var card = (LyricsSliderCard)((System.Windows.Forms.ToolStripControlHost)timing.DropDownItems[0]).Control;
        System.Windows.Forms.Application.DoEvents();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        Assert.True(menu.Visible);
        Assert.True(timing.DropDown.Visible);
        Assert.Equal(menu.BackColor, timing.DropDown.BackColor);
        Assert.Equal(menu.BackColor, card.BackColor);
        Assert.Equal((int)Math.Round(LyricsSliderCard.CardWidth * scale), card.Width);
        Assert.True(card.Slider.ActualWidth > 140,
            $"Slider={card.Slider.ActualWidth}, content={card.ContentView.ActualWidth}, host={card.ClientSize}, scale={scale}");
        for (var index = 0; index < 5; index++) card.IncreaseButton.RaiseEvent(new Wpf.RoutedEventArgs(WpfControls.Button.ClickEvent));
        Assert.Equal(50, widget.LyricsOffsetMilliseconds);
        Assert.True(menu.Visible);
        Assert.True(timing.DropDown.Visible);
        card.Slider.RaiseEvent(new WpfControls.Primitives.DragStartedEventArgs(0, 0)
            { RoutedEvent = WpfControls.Primitives.Thumb.DragStartedEvent });
        card.Slider.Value = 123;
        timing.HideDropDown();
        Assert.Equal(1230, widget.LyricsOffsetMilliseconds);
        Assert.False(timing.DropDown.Visible);
        timing.ShowDropDown();
        Assert.Same(card, ((System.Windows.Forms.ToolStripControlHost)timing.DropDownItems[0]).Control);
        card.IncreaseButton.RaiseEvent(new Wpf.RoutedEventArgs(WpfControls.Button.ClickEvent));
        Assert.Equal(1240, widget.LyricsOffsetMilliseconds);
        menu.Close();
    });

    [Theory]
    [InlineData(true, 1d)]
    [InlineData(false, 1d)]
    [InlineData(true, 1.25d)]
    [InlineData(false, 1.25d)]
    public void LyricEdgesResizeWidthAndHeightIndependently(bool width, double scale) => OnSta(() =>
    {
        var (runtime, widget) = Fixture(scale);
        using var saveTimer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", saveTimer);
        using var form = new DesktopBoxForm(runtime, runtime.Monitors[0]);
        _ = form.Handle;
        var start = width ? new PointF(368, 100) : new PointF(200, 288);
        var finish = width ? new PointF(431, 100) : new PointF(200, 343);
        Assert.Equal(width ? WidgetResizeEdges.Right : WidgetResizeEdges.Bottom, WidgetSizing.HitTest(widget, start.X, start.Y));
        var mouse = new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1, (int)start.X, (int)start.Y, 0);
        Assert.True((bool)Call(form, "HandleWidgetMouseDown", mouse, start)!);
        Call(form, "HandleWidgetMouseMove", finish);
        Call(form, "HandleWidgetMouseUp", mouse, finish);
        Assert.Equal(372 + (width ? Math.Round(63 * scale) / scale : 0), widget.Bounds.Width, precision: 6);
        Assert.Equal(292 + (width ? 0 : Math.Round(55 * scale) / scale), widget.Bounds.Height, precision: 6);
        Assert.Equal(DesktopWidgetSize.Custom, widget.Size);
    });

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

    private static (CrabDeskRuntime Runtime, DesktopWidget Widget) Fixture(double scale = 1)
    {
        var runtime = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
        Set(runtime, "<State>k__BackingField", new CrabDeskState());
        Set(runtime, "<Items>k__BackingField", Array.Empty<DesktopItemRef>());
        Set(runtime, "<Monitors>k__BackingField", new[] { new MonitorLayout { Id = "test", DeviceName = "test", IsPrimary = true, DpiScale = scale,
            Bounds = new(0, 0, 1000, 700), WorkArea = new(0, 0, 1000, 700), PixelBounds = new(0, 0, 1000 * scale, 700 * scale), PixelWorkArea = new(0, 0, 1000 * scale, 700 * scale) } });
        Set(runtime, "_beginInvoke", (Action<Action>)(_ => { }));
        Set(runtime, "_localLyrics", new Dictionary<Guid, (string, string, WidgetLyricsDocument?)>());
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Lyrics, Size = DesktopWidgetSize.Panel, MonitorId = "test", Bounds = new(0, 0, 372, 292) };
        runtime.State.Widgets.Add(widget);
        return (runtime, widget);
    }

    private static void ExportSliderCard(LyricsSliderCard card, double scale, string name)
    {
        var view = card.ContentView;
        var visibility = view.Visibility;
        card.Child = null;
        // Removing an ElementHost child suspends WPF layout. Attaching it to
        // a fresh visual root resumes layout before measuring the export.
        var root = new WpfControls.Border { Child = view };
        try
        {
            Media.VisualTreeHelper.SetRootDpi(root, new Wpf.DpiScale(scale, scale));
            view.Visibility = Wpf.Visibility.Visible;
            card.Slider.ApplyTemplate();
            card.ResetButton.ApplyTemplate();
            root.Measure(new Wpf.Size(LyricsSliderCard.CardWidth, LyricsSliderCard.CardHeight));
            root.Arrange(new Wpf.Rect(0, 0, LyricsSliderCard.CardWidth, LyricsSliderCard.CardHeight));
            root.UpdateLayout();
            var track = Assert.IsType<WpfControls.Primitives.Track>(card.Slider.Template.FindName("PART_Track", card.Slider));
            Assert.NotNull(track.Thumb);
            Assert.True(track.ActualWidth > 140);
            var sliderPosition = card.Slider.TranslatePoint(new Wpf.Point(), root);
            var decreasePosition = card.DecreaseButton.TranslatePoint(new Wpf.Point(), root);
            var increasePosition = card.IncreaseButton.TranslatePoint(new Wpf.Point(), root);
            Assert.True(decreasePosition.X + card.DecreaseButton.ActualWidth < sliderPosition.X);
            Assert.True(sliderPosition.X + card.Slider.ActualWidth < increasePosition.X);
            foreach (var button in new[] { card.DecreaseButton, card.IncreaseButton })
            {
                Assert.Equal(24, button.ActualWidth);
                Assert.Equal(24, button.ActualHeight);
                var position = button.TranslatePoint(new Wpf.Point(), root);
                Assert.True(position.X + button.ActualWidth <= LyricsSliderCard.CardWidth);
                Assert.True(position.Y + button.ActualHeight <= LyricsSliderCard.CardHeight);
            }
            Assert.Equal(64, card.ResetButton.ActualWidth);
            Assert.Equal(24, card.ResetButton.ActualHeight);
            var resetPosition = card.ResetButton.TranslatePoint(new Wpf.Point(), root);
            Assert.True(resetPosition.X + card.ResetButton.ActualWidth <= LyricsSliderCard.CardWidth);
            Assert.True(resetPosition.Y + card.ResetButton.ActualHeight <= LyricsSliderCard.CardHeight);
            var output = Environment.GetEnvironmentVariable("CRABDESK_LYRICS_VISUAL_OUTPUT");
            if (string.IsNullOrEmpty(output)) return;
            Directory.CreateDirectory(output);
            var bitmap = new Imaging.RenderTargetBitmap((int)Math.Round(LyricsSliderCard.CardWidth * scale),
                (int)Math.Round(LyricsSliderCard.CardHeight * scale), 96 * scale, 96 * scale, Media.PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new Imaging.PngBitmapEncoder();
            encoder.Frames.Add(Imaging.BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, name + ".png"));
            encoder.Save(stream);
        }
        finally { root.Child = null; view.Visibility = visibility; card.Child = view; }
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
