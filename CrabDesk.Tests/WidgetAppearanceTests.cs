using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class WidgetAppearanceTests
{
    private static readonly BoxAppearance Inherited = new() { Background = "#FF2A2D32", Accent = "#FF4EA1D3" };
    private static readonly DateTimeOffset Noon = new(2026, 11, 11, 12, 0, 0, TimeSpan.FromHours(8));

    [Theory]
    [InlineData(DesktopWidgetKind.Weather)]
    [InlineData(DesktopWidgetKind.Calendar)]
    [InlineData(DesktopWidgetKind.Note)]
    [InlineData(DesktopWidgetKind.Clock)]
    [InlineData(DesktopWidgetKind.Todo)]
    [InlineData(DesktopWidgetKind.Translation)]
    [InlineData(DesktopWidgetKind.Performance)]
    [InlineData(DesktopWidgetKind.Network)]
    [InlineData(DesktopWidgetKind.ControlCenter)]
    [InlineData(DesktopWidgetKind.Lyrics)]
    [InlineData(DesktopWidgetKind.FileShelf)]
    public void NewWidgetsUseTheirDefaultMaterial(DesktopWidgetKind kind)
    {
        var widget = new DesktopWidget { Kind = kind };
        WidgetAppearance.ApplyNewDefaults(widget);
        Assert.Equal(kind == DesktopWidgetKind.Lyrics ? WidgetBackgroundMaterial.Acrylic : WidgetBackgroundMaterial.Solid,
            widget.BackgroundMaterial);
        Assert.Equal(WidgetColorTheme.Signature, widget.ColorTheme);
    }

    [Theory]
    [InlineData(DesktopWidgetKind.Clock)]
    [InlineData(DesktopWidgetKind.Todo)]
    [InlineData(DesktopWidgetKind.Translation)]
    [InlineData(DesktopWidgetKind.Performance)]
    [InlineData(DesktopWidgetKind.Network)]
    [InlineData(DesktopWidgetKind.ControlCenter)]
    [InlineData(DesktopWidgetKind.FileShelf)]
    public void SolidSignatureSurfacesAreNeutralAndReadable(DesktopWidgetKind kind)
    {
        var widget = new DesktopWidget { Kind = kind, BackgroundMaterial = WidgetBackgroundMaterial.Solid };
        var surface = WidgetAppearance.Resolve(widget, Inherited, false, Noon);
        Assert.Equal(1, surface.Opacity);
        Assert.False(surface.Acrylic);
        Assert.Contains(surface.Background, new[] { "#FFFFFFFF", "#FF1C1C1E" });
        Assert.Null(surface.GradientEnd);
        AssertReadable(surface);
    }

    [Fact]
    public void EveryWidgetKindHasAReadablePaletteForAllThemes()
    {
        var backgrounds = new HashSet<string>();
        foreach (var kind in Enum.GetValues<DesktopWidgetKind>().Where(kind => kind != DesktopWidgetKind.Lyrics))
        {
            var widget = new DesktopWidget { Kind = kind, BackgroundMaterial = WidgetBackgroundMaterial.Acrylic };
            foreach (var theme in Enum.GetValues<WidgetColorTheme>())
            {
                widget.ColorTheme = theme;
                var surface = WidgetAppearance.Resolve(widget, Inherited, false, Noon);
                AssertReadable(surface);
                if (theme == WidgetColorTheme.Signature) backgrounds.Add(surface.Background);
                widget.BackgroundMaterial = WidgetBackgroundMaterial.Solid;
                var solid = WidgetAppearance.Resolve(widget, Inherited, true, Noon);
                AssertReadable(solid);
                Assert.Contains(solid.Background, new[] { "#FFFFFFFF", "#FF1C1C1E", "#FF000000" });
                Assert.Null(solid.GradientEnd);
                Assert.Equal(1, solid.Opacity);
                Assert.False(solid.Acrylic);
                widget.BackgroundMaterial = WidgetBackgroundMaterial.Acrylic;
            }
        }
        Assert.Equal(Inherited.Background, Assert.Single(backgrounds));
    }

    [Fact]
    public async Task MissingSavedMaterialKeepsLegacyAppearanceAfterReload()
    {
        // A widget saved before materials existed has no such properties in its
        // JSON. Loading it must leave it on the inherited appearance instead of
        // recolouring a desktop the user already arranged.
        var root = Path.Combine(Path.GetTempPath(), "CrabDesk.WidgetMaterialTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "config.json"), """
            {"SchemaVersion":24,"Boxes":[],"Widgets":[{"Kind":5,"Size":1,"MonitorId":"m1","Bounds":{"X":10,"Y":10,"Width":180,"Height":180}}]}
            """);
        var widget = Assert.Single((await new JsonLayoutStore(root).LoadAsync()).Widgets);
        Assert.Equal(DesktopWidgetKind.Weather, widget.Kind);
        Assert.Equal(WidgetBackgroundMaterial.Inherit, widget.BackgroundMaterial);
        var surface = WidgetAppearance.Resolve(widget, Inherited, WidgetAppearance.UsesAcrylic(widget, true), Noon);
        Assert.Equal(Inherited.Background, surface.Background);
        Assert.Null(surface.GradientEnd);
    }

    [Fact]
    public async Task ExplicitChoicesSurviveConfigurationAndBackupRoundTrips()
    {
        var root = Path.Combine(Path.GetTempPath(), "CrabDesk.WidgetMaterialTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var state = new CrabDeskState();
        state.Widgets.Add(new DesktopWidget
        {
            Kind = DesktopWidgetKind.Note, BackgroundMaterial = WidgetBackgroundMaterial.Acrylic, ColorTheme = WidgetColorTheme.Dark
        });
        state.Widgets.Add(new DesktopWidget
        {
            Kind = DesktopWidgetKind.Calendar, BackgroundMaterial = WidgetBackgroundMaterial.Solid, ColorTheme = WidgetColorTheme.Light
        });
        var store = new JsonLayoutStore(root);
        await store.SaveAsync(state);
        var loaded = await store.LoadAsync();
        Assert.Equal(WidgetBackgroundMaterial.Acrylic, loaded.Widgets[0].BackgroundMaterial);
        Assert.Equal(WidgetColorTheme.Dark, loaded.Widgets[0].ColorTheme);

        var backups = new JsonBackupService(Path.Combine(root, "backups"));
        var info = await backups.CreateAsync(loaded);
        var restored = await backups.LoadAsync(info.Path);
        Assert.Equal(WidgetBackgroundMaterial.Acrylic, restored.Widgets[0].BackgroundMaterial);
        Assert.Equal(WidgetBackgroundMaterial.Solid, restored.Widgets[1].BackgroundMaterial);
        Assert.Equal(WidgetColorTheme.Light, restored.Widgets[1].ColorTheme);
    }

    [Fact]
    public void UndefinedMaterialAndThemeAreRepaired()
    {
        var widget = new DesktopWidget
        {
            Kind = DesktopWidgetKind.Note,
            BackgroundMaterial = (WidgetBackgroundMaterial)9,
            ColorTheme = (WidgetColorTheme)7
        };
        WidgetAppearance.Normalize(widget);
        Assert.Equal(WidgetBackgroundMaterial.Inherit, widget.BackgroundMaterial);
        Assert.Equal(WidgetColorTheme.Signature, widget.ColorTheme);
    }

    [Fact]
    public void SolidIgnoresGlobalAcrylicAndInheritedTransparency()
    {
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Note, BackgroundMaterial = WidgetBackgroundMaterial.Solid };
        var surface = WidgetAppearance.Resolve(widget, new() { BackgroundOpacity = .2 }, WidgetAppearance.UsesAcrylic(widget, true), Noon);
        Assert.Equal(1, surface.Opacity);
        Assert.False(surface.Acrylic);
        Assert.Equal("#FFFFFFFF", surface.Background);
        Assert.False(WidgetAppearance.UsesAcrylic(widget, true));
    }

    [Fact]
    public void ExplicitAcrylicWorksWithGlobalAcrylicOffAndFallsBackOpaquely()
    {
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Calendar, BackgroundMaterial = WidgetBackgroundMaterial.Acrylic };
        var settings = new WidgetAcrylicSettings { FollowBoxAppearance = false };
        var surface = WidgetAppearance.Resolve(widget, Inherited, WidgetAppearance.UsesAcrylic(widget, false, settings), Noon, settings);
        Assert.True(surface.Acrylic);
        Assert.Equal(WidgetAcrylicSettings.DefaultBackground, surface.Background);
        Assert.Equal(.45, surface.Opacity);
        Assert.Null(surface.GradientEnd);
        Assert.True(WidgetAppearance.UsesAcrylic(widget, false, settings));
        var fallback = WidgetAppearance.Resolve(widget, Inherited, false, widgetAcrylic: settings);
        Assert.False(fallback.Acrylic);
        Assert.Equal(1, fallback.Opacity);
        Assert.Equal(surface.Background, fallback.Background);
    }

    [Fact]
    public void SwitchingMaterialPreservesThemeAndEachMaterialsAppearance()
    {
        var inherited = new BoxAppearance { Background = "#FF202020", Accent = "#FFFF0000", BackgroundOpacity = .55 };
        foreach (var kind in Enum.GetValues<DesktopWidgetKind>().Where(kind => kind != DesktopWidgetKind.Lyrics))
        foreach (var theme in Enum.GetValues<WidgetColorTheme>())
        {
            var widget = new DesktopWidget { Kind = kind, ColorTheme = theme, BackgroundMaterial = WidgetBackgroundMaterial.Solid };
            var solid = WidgetAppearance.Resolve(widget, inherited, false, Noon);
            widget.BackgroundMaterial = WidgetBackgroundMaterial.Acrylic;
            var acrylic = WidgetAppearance.Resolve(widget, inherited, true, Noon);
            Assert.Equal(.55, acrylic.Opacity);
            Assert.True(acrylic.Acrylic);
            Assert.Equal(acrylic with { Acrylic = false }, WidgetAppearance.Resolve(widget, inherited, false, Noon));
            Assert.Equal(theme, widget.ColorTheme);
            widget.BackgroundMaterial = WidgetBackgroundMaterial.Solid;
            Assert.Equal(solid, WidgetAppearance.Resolve(widget, inherited, true, Noon));
            widget.BackgroundMaterial = WidgetBackgroundMaterial.Inherit;
            var legacy = WidgetAppearance.Resolve(widget, inherited, true, Noon);
            Assert.Equal(inherited.Background, legacy.Background);
            Assert.Equal(inherited.Accent, legacy.Accent);
            Assert.Null(legacy.GradientEnd);
        }
    }

    [Fact]
    public void TransparentLyricsNeverRequestAcrylic()
    {
        var widget = new DesktopWidget
        {
            Kind = DesktopWidgetKind.Lyrics,
            BackgroundMaterial = WidgetBackgroundMaterial.Acrylic,
            LyricsTransparentBackground = true
        };
        Assert.False(WidgetAppearance.UsesAcrylic(widget, true));
    }

    [Theory]
    [InlineData(WidgetBackgroundMaterial.Inherit, false)]
    [InlineData(WidgetBackgroundMaterial.Solid, false)]
    [InlineData(WidgetBackgroundMaterial.Acrylic, false)]
    [InlineData(WidgetBackgroundMaterial.Inherit, true)]
    [InlineData(WidgetBackgroundMaterial.Solid, true)]
    [InlineData(WidgetBackgroundMaterial.Acrylic, true)]
    public void LyricsNormalizationPreservesTheBackgroundMode(WidgetBackgroundMaterial saved, bool transparent)
    {
        var widget = new DesktopWidget
        {
            Kind = DesktopWidgetKind.Lyrics, BackgroundMaterial = saved, LyricsTransparentBackground = transparent,
            Text = "保留内容", LyricsFontSize = 28
        };
        var state = new CrabDeskState();
        state.Settings.Appearance.WidgetAcrylic.FollowBoxAppearance = false;
        state.Widgets.Add(widget);
        DesktopWidgets.Normalize(state);
        Assert.Equal(WidgetBackgroundMaterial.Acrylic, widget.BackgroundMaterial);
        Assert.Equal(transparent, widget.LyricsTransparentBackground);
        Assert.Equal(!transparent, WidgetAppearance.UsesAcrylic(widget, false, state.Settings.Appearance.WidgetAcrylic));
        Assert.Equal(!transparent, WidgetAppearance.RequiresAcrylic(state));
        Assert.Equal("保留内容", widget.Text);
        Assert.Equal(28, widget.LyricsFontSize);
    }

    [Theory]
    [InlineData(WidgetBackgroundMaterial.Inherit)]
    [InlineData(WidgetBackgroundMaterial.Solid)]
    [InlineData(WidgetBackgroundMaterial.Acrylic)]
    public void LyricsResolveAcrylicOrTransparentUsingTheGlobalAppearance(WidgetBackgroundMaterial saved)
    {
        var inherited = new BoxAppearance { Background = "#FFFFFFFF", Accent = "#FFAABBCC", BackgroundOpacity = .9 };
        var settings = new WidgetAcrylicSettings { FollowBoxAppearance = false, Background = "#FF20242C", BackgroundOpacity = .4 };
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Lyrics, BackgroundMaterial = saved };
        Assert.True(WidgetAppearance.UsesAcrylic(widget, false, settings));
        var surface = WidgetAppearance.Resolve(widget, inherited, true, widgetAcrylic: settings);
        Assert.Equal(settings.Background, surface.Background);
        Assert.Equal(.4, surface.Opacity);
        Assert.True(surface.Acrylic);
        Assert.Equal(surface with { Opacity = 1, Acrylic = false },
            WidgetAppearance.Resolve(widget, inherited, false, widgetAcrylic: settings));
        widget.LyricsTransparentBackground = true;
        Assert.Equal(surface with { Opacity = 0, Acrylic = false },
            WidgetAppearance.Resolve(widget, inherited, true, widgetAcrylic: settings));
        Assert.False(WidgetAppearance.UsesAcrylic(widget, false));
    }

    [Fact]
    public void AcrylicHostIsRequestedOnlyForEligibleWidgets()
    {
        var state = new CrabDeskState();
        state.Settings.Appearance.UseAcrylicBoxes = false;
        state.Settings.Appearance.WidgetAcrylic.FollowBoxAppearance = false;
        state.Widgets.Add(new DesktopWidget { Kind = DesktopWidgetKind.Weather, BackgroundMaterial = WidgetBackgroundMaterial.Solid });
        Assert.False(WidgetAppearance.RequiresAcrylic(state));
        state.Widgets.Add(new DesktopWidget { Kind = DesktopWidgetKind.Clock, BackgroundMaterial = WidgetBackgroundMaterial.Acrylic });
        Assert.True(WidgetAppearance.RequiresAcrylic(state));
        state.Widgets.Clear();
        state.Settings.Appearance.UseAcrylicBoxes = true;
        Assert.True(WidgetAppearance.RequiresAcrylic(state));
    }

    [Fact]
    public void WeatherUsesTheSharedMaterialThroughoutTheDay()
    {
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Weather, BackgroundMaterial = WidgetBackgroundMaterial.Acrylic };
        widget.Weather = new(21, 14, 25, 0, Noon)
        {
            Sunrise = new(2026, 11, 11, 6, 30, 0, TimeSpan.FromHours(8)),
            Sunset = new(2026, 11, 11, 17, 10, 0, TimeSpan.FromHours(8))
        };
        var day = WidgetAppearance.Resolve(widget, Inherited, false, Noon);
        var night = WidgetAppearance.Resolve(widget, Inherited, false, new(2026, 11, 11, 21, 0, 0, TimeSpan.FromHours(8)));
        Assert.Null(day.GradientEnd);
        Assert.Equal(day, night);
        AssertReadable(day);
        AssertReadable(night);
    }

    [Theory]
    [InlineData(61)] // rain
    [InlineData(3)]  // overcast
    [InlineData(71)] // snow
    public void WeatherConditionsSelectAReadableSignaturePalette(int weatherCode)
    {
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Weather, BackgroundMaterial = WidgetBackgroundMaterial.Acrylic };
        widget.Weather = new(16, 12, 19, weatherCode, Noon);
        AssertReadable(WidgetAppearance.Resolve(widget, Inherited, false, Noon));
    }

    [Theory]
    [InlineData(DesktopWidgetKind.Calendar)]
    [InlineData(DesktopWidgetKind.Note)]
    [InlineData(DesktopWidgetKind.Weather)]
    public void EverySolidThemeKeepsItsInkReadable(DesktopWidgetKind kind)
    {
        var widget = new DesktopWidget { Kind = kind, BackgroundMaterial = WidgetBackgroundMaterial.Solid };
        foreach (var theme in Enum.GetValues<WidgetColorTheme>())
        {
            widget.ColorTheme = theme;
            var surface = WidgetAppearance.Resolve(widget, Inherited, false, Noon);
            Assert.Equal(1, surface.Opacity);
            Assert.False(surface.Acrylic);
            AssertReadable(surface);
        }
    }

    // Mirrors the runtime's automatic ink: whichever of white or near-black
    // contrasts better with the card colour wins, and it must stay legible.
    private static void AssertReadable(WidgetSurfaceAppearance surface)
    {
        Assert.NotNull(surface.Background);
        var ink = Contrast(surface.Background, "#FFFFFFFF") >= Contrast(surface.Background, "#FF1F2329") ? "#FFFFFFFF" : "#FF1F2329";
        Assert.True(Contrast(surface.Background, ink) >= 4.5, $"{surface.Background} with {ink}");
        if (surface.GradientEnd is { } end)
            Assert.True(Contrast(end, ink) >= 4.5, $"{end} with {ink}");
    }

    private static double Contrast(string first, string second)
    {
        var one = RelativeLuminance(first);
        var two = RelativeLuminance(second);
        return (Math.Max(one, two) + .05) / (Math.Min(one, two) + .05);
    }

    private static double RelativeLuminance(string hex)
    {
        var text = hex.TrimStart('#');
        if (text.Length == 8) text = text[2..];
        static double Linearize(string channel)
        {
            var normalized = Convert.ToInt32(channel, 16) / 255d;
            return normalized <= 0.04045d ? normalized / 12.92d : Math.Pow((normalized + 0.055d) / 1.055d, 2.4d);
        }
        return 0.2126d * Linearize(text[..2]) + 0.7152d * Linearize(text[2..4]) + 0.0722d * Linearize(text[4..6]);
    }
}
