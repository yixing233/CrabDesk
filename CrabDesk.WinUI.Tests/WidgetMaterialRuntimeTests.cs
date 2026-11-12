using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using CrabDesk.Core;
using CrabDesk.Runtime;
using CrabDesk.WinUI.Views;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class WidgetMaterialRuntimeTests
{
    [Theory]
    [InlineData(1d)]
    [InlineData(1.5d)]
    public void WidgetCornersMatchPaintBlurAndNativeRegionIndependentlyOfBoxes(double scale) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture(scale);
        runtime.State.Settings.Appearance.UseAcrylicBoxes = true;
        runtime.State.Settings.Appearance.CornerRadius = 20;
        runtime.State.Settings.Appearance.WidgetCornerRadius = 0;
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Note, MonitorId = monitor.Id,
            Bounds = new(20, 20, 180, 180), BackgroundMaterial = WidgetBackgroundMaterial.Solid };
        var box = new DesktopBox { MonitorId = monitor.Id, Bounds = new(240, 20, 180, 180) };
        runtime.State.Widgets.Add(widget);
        runtime.State.Boxes.Add(box);
        using var surface = new DesktopBoxForm(runtime, monitor);
        surface.SetAcrylicBackground(true);
        using var square = Render(surface, widget, 220, scale: scale, clear: Color.Transparent);
        Assert.Equal(255, square.GetPixel((int)(22 * scale), (int)(22 * scale)).A);
        widget.BackgroundMaterial = WidgetBackgroundMaterial.Acrylic;
        var regions = surface.GetAcrylicRegions().ToDictionary(region => region.Id);
        Assert.Equal(0, regions[widget.Id].CornerRadius);
        Assert.Equal((float)(20 * scale), regions[box.Id].CornerRadius);
        var window = surface.Handle;
        void AssertRegion(bool widgetVisible, bool boxVisible)
        {
            Assert.True((bool)typeof(DesktopBoxForm).GetMethod("UpdateWindowRegion", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(surface, null)!);
            using var buffer = new Bitmap(1, 1);
            using var graphics = Graphics.FromImage(buffer);
            using var storage = new Region(new Rectangle(0, 0, 1, 1));
            var handle = storage.GetHrgn(graphics);
            try
            {
                Assert.NotEqual(0, GetWindowRgn(window, handle));
                using var actual = Region.FromHrgn(handle);
                Assert.Equal(widgetVisible, actual.IsVisible((float)(22 * scale), (float)(22 * scale)));
                Assert.Equal(boxVisible, actual.IsVisible((float)(242 * scale), (float)(22 * scale)));
            }
            finally { storage.ReleaseHrgn(handle); }
        }
        AssertRegion(true, false);
        runtime.State.Settings.Appearance.WidgetCornerRadius = 20;
        runtime.State.Settings.Appearance.CornerRadius = 0;
        using var rounded = Render(surface, widget, 220, scale: scale, clear: Color.Transparent);
        Assert.Equal(0, rounded.GetPixel((int)(22 * scale), (int)(22 * scale)).A);
        regions = surface.GetAcrylicRegions().ToDictionary(region => region.Id);
        Assert.Equal((float)(20 * scale), regions[widget.Id].CornerRadius);
        Assert.Equal(0, regions[box.Id].CornerRadius);
        AssertRegion(false, true);
    });

    [DllImport("user32.dll")]
    private static extern int GetWindowRgn(IntPtr window, IntPtr region);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AcrylicKeepsLegacyContentDesignForBothBackgroundSources(bool followBox) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        runtime.State.Settings.Appearance.UseAcrylicBoxes = true;
        runtime.State.Settings.Appearance.ShowBorder = false;
        runtime.State.Settings.Appearance.WidgetAcrylic.FollowBoxAppearance = followBox;
        runtime.State.Settings.Appearance.WidgetAcrylic.BackgroundOpacity = .27;
        runtime.State.Settings.DefaultBoxAppearance = new()
        {
            Background = "#FF123456", Accent = "#FFABCDEF", BackgroundOpacity = .27
        };
        using var surface = new DesktopBoxForm(runtime, monitor);
        surface.SetAcrylicBackground(true);
        foreach (var kind in Enum.GetValues<DesktopWidgetKind>()
                     .Where(kind => kind is not (DesktopWidgetKind.Clock or DesktopWidgetKind.Lyrics)))
        {
            var widget = Sample(kind, monitor.Id);
            widget.BackgroundMaterial = WidgetBackgroundMaterial.Inherit;
            runtime.State.Widgets.Clear();
            runtime.State.Widgets.Add(widget);
            var width = (int)widget.Bounds.Width + 40;
            var height = (int)widget.Bounds.Height + 40;
            using var legacy = Render(surface, widget, width, height, clear: Color.Transparent);
            widget.BackgroundMaterial = WidgetBackgroundMaterial.Acrylic;
            using var acrylic = Render(surface, widget, width, height, clear: Color.Transparent);
            for (var y = 0; y < height; y += 3)
            for (var x = 0; x < width; x += 3)
                Assert.Equal(legacy.GetPixel(x, y).ToArgb(), acrylic.GetPixel(x, y).ToArgb());

            if (kind is DesktopWidgetKind.ControlCenter or DesktopWidgetKind.Translation)
            {
                var opaque = 0;
                for (var y = 24; y < height - 24; y += 4)
                for (var x = 24; x < width - 24; x += 4)
                    if (acrylic.GetPixel(x, y).A == 255) opaque++;
                Assert.True(opaque > (width - 48) * (height - 48) / 16 * .2,
                    $"{kind} must retain opaque content panels.");
            }
        }
    });

    [Fact]
    public void AcrylicClockKeepsItsOpaqueNeutralDial() => OnSta(() =>
    {
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Clock, BackgroundMaterial = WidgetBackgroundMaterial.Acrylic };
        using var image = new Bitmap(180, 180);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.Transparent);
            DesktopBoxForm.DrawClockWidget(graphics, widget, new(0, 0, 180, 180), Color.White, Color.Gray,
                new DateTime(2026, 11, 12, 10, 10, 0), Color.FromArgb(18, 52, 86), Color.Cyan);
        }
        Assert.Equal(Color.FromArgb(26, 26, 26).ToArgb(), image.GetPixel(65, 105).ToArgb());
    });

    [Fact]
    public void DesktopRegionsUseOneSelectedBlurSourceForEveryAcrylicWidget() => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        runtime.State.Settings.Appearance.AcrylicBlurStrength = 20;
        runtime.State.Settings.Appearance.WidgetAcrylic.BlurStrength = 85;
        var note = new DesktopWidget { Kind = DesktopWidgetKind.Note, MonitorId = monitor.Id,
            Bounds = new(20, 20, 180, 180), BackgroundMaterial = WidgetBackgroundMaterial.Acrylic };
        var lyrics = new DesktopWidget { Kind = DesktopWidgetKind.Lyrics, MonitorId = monitor.Id,
            Bounds = new(220, 20, 180, 180), BackgroundMaterial = WidgetBackgroundMaterial.Acrylic };
        var inherited = new DesktopWidget { Kind = DesktopWidgetKind.Clock, MonitorId = monitor.Id,
            Bounds = new(420, 20, 180, 180), BackgroundMaterial = WidgetBackgroundMaterial.Inherit };
        runtime.State.Widgets.AddRange([note, lyrics, inherited]);
        using var surface = new DesktopBoxForm(runtime, monitor);
        surface.SetAcrylicBackground(true);
        var regions = surface.GetAcrylicRegions().ToDictionary(region => region.Id);
        Assert.Equal(85f, regions[note.Id].BlurStrength);
        Assert.Equal(85f, regions[lyrics.Id].BlurStrength);
        Assert.Equal(85f, regions[inherited.Id].BlurStrength);
        runtime.State.Settings.Appearance.WidgetAcrylic.BlurStrength = 10;
        regions = surface.GetAcrylicRegions().ToDictionary(region => region.Id);
        Assert.Equal(10f, regions[note.Id].BlurStrength);
        Assert.Equal(10f, regions[lyrics.Id].BlurStrength);
        Assert.Equal(10f, regions[inherited.Id].BlurStrength);
        runtime.State.Settings.Appearance.WidgetAcrylic.FollowBoxAppearance = true;
        regions = surface.GetAcrylicRegions().ToDictionary(region => region.Id);
        Assert.Equal(20f, regions[note.Id].BlurStrength);
        Assert.Equal(20f, regions[lyrics.Id].BlurStrength);
        Assert.Equal(20f, regions[inherited.Id].BlurStrength);
        surface.SetWidgetAcrylicHost(true);
        Assert.Empty(surface.GetAcrylicRegions());
        runtime.State.Settings.Appearance.WidgetAcrylic.FollowBoxAppearance = false;
        regions = surface.GetAcrylicRegions().ToDictionary(region => region.Id);
        Assert.Contains(inherited.Id, regions.Keys);
        lyrics.LyricsTransparentBackground = true;
        Assert.Equal(2, surface.GetAcrylicRegions().Count());
    });

    [Fact]
    public void NewWidgetsUseTheirDefaultMaterialWhileExistingCardsAreLeftAlone()
    {
        var (runtime, _) = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        var created = new Dictionary<DesktopWidgetKind, DesktopWidget>();
        foreach (var kind in Enum.GetValues<DesktopWidgetKind>())
            created[kind] = runtime.AddWidget(kind);
        foreach (var (kind, widget) in created)
        {
            Assert.Equal(kind == DesktopWidgetKind.Lyrics ? WidgetBackgroundMaterial.Acrylic : WidgetBackgroundMaterial.Solid,
                widget.BackgroundMaterial);
            Assert.Equal(WidgetColorTheme.Signature, widget.ColorTheme);
            var surface = WidgetAppearance.Resolve(widget, new BoxAppearance(), false);
            Assert.Equal(1, surface.Opacity);
            Assert.False(surface.Acrylic);
            Assert.False(string.IsNullOrWhiteSpace(surface.Background), $"{kind} has no background.");
        }

        // A widget saved before materials existed must not be recoloured just
        // because its kind now has a signature palette.
        var legacy = new DesktopWidget { Kind = DesktopWidgetKind.Note, Text = "旧便签" };
        runtime.State.Widgets.Remove(created[DesktopWidgetKind.Note]);
        runtime.State.Widgets.Add(legacy);
        Assert.Same(legacy, runtime.AddWidget(DesktopWidgetKind.Note));
        Assert.Equal(WidgetBackgroundMaterial.Inherit, legacy.BackgroundMaterial);
        Assert.Equal(WidgetColorTheme.Signature, legacy.ColorTheme);
        var legacyClock = new DesktopWidget { Kind = DesktopWidgetKind.Clock };
        runtime.State.Widgets.Remove(created[DesktopWidgetKind.Clock]);
        runtime.State.Widgets.Add(legacyClock);
        Assert.Same(legacyClock, runtime.AddWidget(DesktopWidgetKind.Clock));
        Assert.Equal(WidgetBackgroundMaterial.Inherit, legacyClock.BackgroundMaterial);
    }

    [Fact]
    public void SolidWidgetSkipsBlurWhileAcrylicWidgetKeepsItsRegion() => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        var solid = new DesktopWidget
        {
            Kind = DesktopWidgetKind.Note, MonitorId = monitor.Id, Bounds = new(20, 20, 180, 180),
            BackgroundMaterial = WidgetBackgroundMaterial.Solid
        };
        var acrylic = new DesktopWidget
        {
            Kind = DesktopWidgetKind.Calendar, MonitorId = monitor.Id, Bounds = new(220, 20, 180, 180),
            BackgroundMaterial = WidgetBackgroundMaterial.Acrylic
        };
        runtime.State.Widgets.AddRange([solid, acrylic]);
        using var surface = new DesktopBoxForm(runtime, monitor);
        surface.SetAcrylicBackground(true);
        // The global box switch stays off: only the widget's own choice puts it
        // in the blur pass, and the solid card must not join it.
        Assert.False(runtime.State.Settings.Appearance.UseAcrylicBoxes);
        var regions = surface.GetAcrylicRegions().ToDictionary(region => region.Id);
        Assert.False(regions.ContainsKey(solid.Id));
        Assert.True(regions.ContainsKey(acrylic.Id));
    });

    [Fact]
    public void SolidWidgetPaintsFullyOpaqueOverAnAcrylicDesktop() => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        runtime.State.Settings.Appearance.UseAcrylicBoxes = true;
        runtime.State.Settings.Appearance.WidgetAcrylic.FollowBoxAppearance = true;
        runtime.State.Settings.DefaultBoxAppearance.BackgroundOpacity = .35;
        var solid = new DesktopWidget
        {
            Kind = DesktopWidgetKind.Note, MonitorId = monitor.Id, Bounds = new(20, 20, 180, 180),
            Text = "", BackgroundMaterial = WidgetBackgroundMaterial.Solid
        };
        runtime.State.Widgets.Add(solid);
        using var surface = new DesktopBoxForm(runtime, monitor);
        surface.SetAcrylicBackground(true);
        var opaque = Render(surface, solid, 220);

        // Same card, but inheriting the legacy acrylic tint.
        var inherited = new DesktopWidget { Kind = DesktopWidgetKind.Note, MonitorId = monitor.Id, Bounds = new(20, 20, 180, 180), Text = "" };
        runtime.State.Widgets.Clear(); runtime.State.Widgets.Add(inherited);
        using var legacySurface = new DesktopBoxForm(runtime, monitor);
        legacySurface.SetAcrylicBackground(true);
        var tinted = Render(legacySurface, inherited, 220);

        var solidColor = Color.White;
        // A solid card stays opaque across its background, independently of
        // the legacy acrylic tint.
        Assert.Equal(255, opaque.GetPixel(30, 30).A);
        Assert.Equal(255, opaque.GetPixel(100, 120).A);
        Assert.NotEqual(solidColor.ToArgb(), tinted.GetPixel(30, 30).ToArgb());
        Assert.NotEqual(Color.Magenta.ToArgb(), tinted.GetPixel(100, 40).ToArgb());
    });

    [Fact]
    public void AcrylicWeatherPaintsTheSharedBackgroundAcrossTheCard() => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        var widget = new DesktopWidget
        {
            Kind = DesktopWidgetKind.Weather, MonitorId = monitor.Id, Bounds = new(20, 20, 180, 180),
            BackgroundMaterial = WidgetBackgroundMaterial.Acrylic, ColorTheme = WidgetColorTheme.Signature
        };
        var now = DateTimeOffset.Now;
        runtime.State.Settings.Appearance.WidgetAcrylic.Background = "#FF345678";
        widget.Weather = new(21, 14, 25, 0, now) { Sunrise = now.AddHours(-2), Sunset = now.AddHours(4) };
        runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor);
        surface.SetAcrylicBackground(true);
        using var image = Render(surface, widget, 220);
        var first = image.GetPixel((int)(24 * 1), (int)(24 * 1));
        var last = image.GetPixel((int)(186 * 1), (int)(186 * 1));
        Assert.Equal(first.ToArgb(), last.ToArgb());
    });

    [Theory]
    [InlineData(WidgetColorTheme.Signature, 1d)]
    [InlineData(WidgetColorTheme.Light, 1d)]
    [InlineData(WidgetColorTheme.Dark, 1d)]
    [InlineData(WidgetColorTheme.Signature, 2d)]
    [InlineData(WidgetColorTheme.Light, 2d)]
    [InlineData(WidgetColorTheme.Dark, 2d)]
    public void SolidControlStatesHaveReadableActiveTilesAndNeutralInactiveTiles(WidgetColorTheme theme, double scale) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture(scale);
        var widget = Sample(DesktopWidgetKind.ControlCenter, monitor.Id);
        widget.ColorTheme = theme;
        widget.Controls = widget.Controls! with { WiFiEnabled = true, BluetoothEnabled = true, NightLightEnabled = true };
        runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor);
        using var active = Render(surface, widget, 412, 332, scale);
        widget.Controls = widget.Controls with { WiFiEnabled = false, BluetoothEnabled = false, NightLightEnabled = false };
        using var inactive = Render(surface, widget, 412, 332, scale);
        var body = ControlCenterPresentation.Body(widget.Bounds.Width, widget.Bounds.Height,
            runtime.State.Settings.Appearance.CornerRadius);
        var actions = ControlQuickActions.For(widget);
        for (var index = 0; index < actions.Count; index++)
        {
            if (ControlQuickActions.Toggle(actions[index]) is null) continue;
            var cell = ControlCenterPresentation.QuickAction(body, index, actions.Count);
            var x = (int)((widget.Bounds.X + cell.X + 6) * scale);
            var y = (int)((widget.Bounds.Y + cell.Y + 6) * scale);
            var on = active.GetPixel(x, y);
            var off = inactive.GetPixel(x, y);
            Assert.Equal(255, on.A);
            Assert.Equal(255, off.A);
            Assert.True((1.05 / (Luminance(on) + .05)) >= 4.5, $"{actions[index]} has insufficient contrast on {theme}.");
            Assert.InRange(Math.Max(off.R, Math.Max(off.G, off.B)) - Math.Min(off.R, Math.Min(off.G, off.B)), 0, 2);
            Assert.True(Math.Abs(on.R - off.R) + Math.Abs(on.G - off.G) + Math.Abs(on.B - off.B) > 50);
        }
    });

    [Theory]
    [InlineData(DesktopWidgetKind.Clock)]
    [InlineData(DesktopWidgetKind.Calendar)]
    [InlineData(DesktopWidgetKind.Note)]
    [InlineData(DesktopWidgetKind.Todo)]
    [InlineData(DesktopWidgetKind.Translation)]
    [InlineData(DesktopWidgetKind.Performance)]
    [InlineData(DesktopWidgetKind.Network)]
    [InlineData(DesktopWidgetKind.ControlCenter)]
    [InlineData(DesktopWidgetKind.FileShelf)]
    [InlineData(DesktopWidgetKind.Weather)]
    public void SolidCardsOfEveryKindPaintTheirOwnSurface(DesktopWidgetKind kind) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        var widget = Sample(kind, monitor.Id);
        runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor);
        foreach (var theme in Enum.GetValues<WidgetColorTheme>())
        {
            widget.ColorTheme = theme;
            using var image = Render(surface, widget, (int)widget.Bounds.Width + 40, (int)widget.Bounds.Height + 40,
                clear: Color.FromArgb(226, 231, 236));
            var start = image.GetPixel(24, 24);
            var end = image.GetPixel((int)(widget.Bounds.X + widget.Bounds.Width) - 4,
                (int)(widget.Bounds.Y + widget.Bounds.Height) - 4);
            Assert.Equal(255, start.A);
            Assert.Equal(255, end.A);
            var surfaceColour = WidgetAppearance.Resolve(widget, new BoxAppearance(), false);
            Assert.Equal(ColorTranslator.FromHtml(surfaceColour.Background).ToArgb(), start.ToArgb());
            Assert.Equal(start.ToArgb(), end.ToArgb());
            Assert.InRange(Math.Max(start.R, Math.Max(start.G, start.B)) - Math.Min(start.R, Math.Min(start.G, start.B)), 0, 2);
            Assert.Null(surfaceColour.GradientEnd);
            if (Environment.GetEnvironmentVariable("CRABDESK_WIDGET_SOLID_OUTPUT") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output);
                image.Save(Path.Combine(output, $"solid-{kind}-{theme}.png"));
            }
        }
    });

    [Theory]
    [InlineData(WidgetColorTheme.Signature, 1d)]
    [InlineData(WidgetColorTheme.Light, 1.5d)]
    [InlineData(WidgetColorTheme.Dark, 2d)]
    public void SharedAcrylicRespectsTintAndFallbackAtBothEdges(WidgetColorTheme theme, double scale) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture(scale);
        runtime.State.Settings.DefaultBoxAppearance.BackgroundOpacity = .9;
        runtime.State.Settings.Appearance.WidgetAcrylic.BackgroundOpacity = .27;
        runtime.State.Settings.Appearance.WidgetAcrylic.Background = "#FF345678";
        runtime.State.Settings.Appearance.ShowBorder = false;
        using var surface = new DesktopBoxForm(runtime, monitor);
        foreach (var kind in Enum.GetValues<DesktopWidgetKind>().Where(kind => kind != DesktopWidgetKind.Lyrics))
        {
            var widget = new DesktopWidget { Kind = kind, MonitorId = monitor.Id, Bounds = new(20, 20, 180, 180),
                BackgroundMaterial = WidgetBackgroundMaterial.Acrylic, ColorTheme = theme };
            runtime.State.Widgets.Clear();
            runtime.State.Widgets.Add(widget);
            foreach (var effects in new[] { true, false })
            {
                surface.SetAcrylicBackground(effects);
                using var image = Render(surface, widget, 220, scale: scale, clear: Color.Transparent);
                var left = image.GetPixel((int)(24 * scale), (int)(110 * scale));
                var right = image.GetPixel((int)(196 * scale), (int)(110 * scale));
                var expected = effects ? (int)Math.Round(.27 * 255) : 255;
                Assert.InRange((int)left.A, expected - 1, expected + 1);
                Assert.InRange((int)right.A, expected - 1, expected + 1);
                Assert.Equal(left.ToArgb(), right.ToArgb());
                Assert.InRange((int)left.R, 0x34 - 2, 0x34 + 2);
                Assert.InRange((int)left.G, 0x56 - 2, 0x56 + 2);
                Assert.InRange((int)left.B, 0x78 - 2, 0x78 + 2);
                Assert.Equal(0, image.GetPixel(0, 0).A);
            }
        }
    });

    [Theory]
    [InlineData(1d)]
    [InlineData(1.5d)]
    [InlineData(2d)]
    public void LyricsModesControlFillAndBlurOnTheirOwn(double scale) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture(scale);
        runtime.State.Settings.DefaultBoxAppearance.BackgroundOpacity = .45;
        runtime.State.Settings.Appearance.ShowBorder = false;
        var widget = Sample(DesktopWidgetKind.Lyrics, monitor.Id);
        runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor);
        surface.SetAcrylicBackground(true);
        surface.SetWidgetAcrylicHost(true);
        Assert.False(runtime.State.Settings.Appearance.UseAcrylicBoxes);
        Assert.Equal(widget.Id, Assert.Single(surface.GetAcrylicRegions()).Id);
        using var acrylic = Render(surface, widget, 412, 332, scale, Color.Transparent);
        Assert.InRange((int)acrylic.GetPixel((int)(24 * scale), (int)(145 * scale)).A, 114, 116);
        widget.LyricsTransparentBackground = true;
        Assert.Empty(surface.GetAcrylicRegions());
        using var transparent = Render(surface, widget, 412, 332, scale, Color.Transparent);
        Assert.Equal(0, transparent.GetPixel((int)(24 * scale), (int)(145 * scale)).A);
        widget.LyricsTransparentBackground = false;
        surface.SetAcrylicBackground(false);
        using var fallback = Render(surface, widget, 412, 332, scale, Color.Transparent);
        Assert.Equal(255, fallback.GetPixel((int)(24 * scale), (int)(145 * scale)).A);
        if (scale == 1 && Environment.GetEnvironmentVariable("CRABDESK_WIDGET_SOLID_OUTPUT") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            acrylic.Save(Path.Combine(output, "lyrics-Acrylic.png"));
            transparent.Save(Path.Combine(output, "lyrics-Transparent.png"));
        }
    });

    [Theory]
    [InlineData(WidgetColorTheme.Signature, 1d)]
    [InlineData(WidgetColorTheme.Light, 1.5d)]
    [InlineData(WidgetColorTheme.Dark, 2d)]
    public void SolidClockFaceIsNeutralAndDistinctFromItsCard(WidgetColorTheme theme, double scale) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture(scale);
        var widget = Sample(DesktopWidgetKind.Clock, monitor.Id);
        widget.ColorTheme = theme;
        runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor);
        using var image = Render(surface, widget, 220, scale: scale);
        var face = 0;
        var sampled = 0;
        var card = ColorTranslator.FromHtml(WidgetAppearance.Resolve(widget, new BoxAppearance(), false).Background);
        // Hands may cross this area at any time. The face still needs a
        // visible, neutral step from its surrounding card.
        for (var y = 80; y < 140; y += 4)
        for (var x = 80; x < 140; x += 4)
        {
            var pixel = image.GetPixel((int)(x * scale), (int)(y * scale));
            sampled++;
            if (Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) - Math.Min(pixel.R, Math.Min(pixel.G, pixel.B)) <= 2 &&
                Math.Abs(pixel.R - card.R) >= 6) face++;
        }
        Assert.True(face > sampled * .7, $"The {theme} dial lost its face separation, scale={scale}.");
    });

    [Fact]
    public void WidgetAppearanceControlsLiveInTheSharedCardSettings()
    {
        var root = SolutionDirectory();
        var xaml = File.ReadAllText(Path.Combine(root, "CrabDesk.WinUI", "Views", "WidgetsPage.xaml"));
        foreach (var id in new[] { "WidgetMaterialPicker", "WidgetThemePicker", "WidgetAppearanceSettings" })
            Assert.Contains(id, xaml);
        var source = File.ReadAllText(Path.Combine(root, "CrabDesk.WinUI", "Views", "WidgetsPage.Appearance.cs"));
        // Existing widgets must open on their saved material, and the hint has
        // to explain that solid never inherits the global tint.
        Assert.Contains("widget.BackgroundMaterial", source);
        Assert.Contains("_loading", source);
        Assert.Contains("widget.Kind != DesktopWidgetKind.Lyrics", source);
        var document = System.Xml.Linq.XDocument.Parse(xaml);
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var materialPicker = document.Descendants(ui + "ComboBox")
            .Single(element => (string?)element.Attribute(x + "Name") == "WidgetMaterialPicker");
        Assert.Equal(new[] { "实色", "亚克力" },
            materialPicker.Elements(ui + "ComboBoxItem").Select(element => (string?)element.Attribute("Content")));
        var lyricsMode = document.Descendants(ui + "ToggleSwitch")
            .Single(element => (string?)element.Attribute(x + "Name") == "LyricsTransparentBackground");
        Assert.Equal("亚克力", (string?)lyricsMode.Attribute("OffContent"));
        Assert.Equal("透明", (string?)lyricsMode.Attribute("OnContent"));
    }

    private static DesktopWidget Sample(DesktopWidgetKind kind, string monitorId)
    {
        var now = DateTimeOffset.Now;
        var size = DesktopWidgets.DefaultSize(kind);
        var (width, height) = DesktopWidgets.Dimensions(size);
        var media = new ControlCenterMedia("此刻的旋律", "CrabDesk", "", false, true, true, true)
        { Duration = TimeSpan.FromMinutes(3), Position = TimeSpan.FromSeconds(7), CapturedAt = now };
        return new DesktopWidget
        {
            Kind = kind, MonitorId = monitorId, Size = size, Bounds = new(20, 20, width, height),
            BackgroundMaterial = kind == DesktopWidgetKind.Lyrics ? WidgetBackgroundMaterial.Acrylic : WidgetBackgroundMaterial.Solid,
            ShowSeconds = true,
            Text = "今天的灵感\n整理手头的工作。\n留一点时间给自己。",
            Translation = "Make room for a new idea.",
            Tasks = [new() { Text = "整理今天的计划" }, new() { Text = "完成第一项任务", IsCompleted = true }],
            Performance = new(now, 24, 12, 9L << 30, 16L << 30, 8, 2_400_000, 384_000, 240L << 30, 512L << 30),
            Network = new(now) { IsConnected = true, NetworkType = "以太网", DownloadBytesPerSecond = 2_400_000,
                UploadBytesPerSecond = 384_000, IPv4 = "192.168.1.100", IPv6 = "2001:db8::10", LatencyMilliseconds = 18 },
            Controls = new() { WiFiEnabled = true, BluetoothEnabled = true, NightLightEnabled = false, VolumePercent = 42,
                VolumeMuted = false, BrightnessPercent = 65, OutputDeviceName = "耳机", Media = media },
            Lyrics = new(media, new([
                new("让思绪慢慢展开", 0, 5000), new("留一点时间给自己", 5000, 10000),
                new("听见生活里的旋律", 10000, 15000), new("让今天多一些色彩", 15000, 20000)
            ], "示例歌词")),
            FileShelfItems = kind == DesktopWidgetKind.FileShelf ? FileShelfSettingsPresentation.SampleItems() : [],
            Location = new("北京", "北京", "中国", 39.9, 116.4),
            Weather = new(22, 16, 25, 2, now) { FeelsLike = 21, Humidity = 52, WindSpeed = 8, Sunrise = now.AddHours(-2), Sunset = now.AddHours(4) }
        };
    }

    private static Bitmap Render(DesktopBoxForm surface, DesktopWidget widget, int size, int? height = null,
        double scale = 1, Color? clear = null)
    {
        var image = new Bitmap((int)Math.Ceiling(size * scale), (int)Math.Ceiling((height ?? size) * scale));
        using var graphics = Graphics.FromImage(image);
        graphics.Clear(clear ?? Color.Magenta);
        graphics.ScaleTransform((float)scale, (float)scale);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        typeof(DesktopBoxForm).GetMethod("DrawWidget", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(surface, [graphics, widget]);
        return image;
    }

    private static (CrabDeskRuntime, MonitorLayout) Fixture(double scale = 1)
    {
        var runtime = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
        var monitor = new MonitorLayout
        {
            Id = "widget-material-test", DeviceName = "widget-material-test", IsPrimary = true, DpiScale = scale,
            Bounds = new(0, 0, 1000, 700), WorkArea = new(0, 0, 1000, 700),
            PixelBounds = new(0, 0, 1000 * scale, 700 * scale), PixelWorkArea = new(0, 0, 1000 * scale, 700 * scale)
        };
        var state = new CrabDeskState();
        state.Settings.Appearance.WidgetAcrylic.FollowBoxAppearance = false;
        Set(runtime, "<State>k__BackingField", state);
        Set(runtime, "<Items>k__BackingField", Array.Empty<DesktopItemRef>());
        Set(runtime, "<Monitors>k__BackingField", new[] { monitor });
        return (runtime, monitor);
    }

    private static void Set(object instance, string name, object value) =>
        instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);

    private static double Luminance(Color color)
    {
        static double Linear(byte value)
        {
            var normalized = value / 255d;
            return normalized <= .04045 ? normalized / 12.92 : Math.Pow((normalized + .055) / 1.055, 2.4);
        }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }

    private static string SolutionDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CrabDesk.sln"))) return directory.FullName;
        throw new InvalidOperationException("CrabDesk.sln not found.");
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { error = exception; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
