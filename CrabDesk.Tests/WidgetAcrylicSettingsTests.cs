using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class WidgetAcrylicSettingsTests
{
    [Theory]
    [InlineData("#FF20242C")]
    [InlineData("#FFF5F5F5")]
    public void EveryAcrylicWidgetSharesMaterialRegardlessOfThemeAndBoxes(string background)
    {
        var settings = new WidgetAcrylicSettings { FollowBoxAppearance = false, Background = background, BackgroundOpacity = .23, BlurStrength = 81 };
        var inherited = new BoxAppearance { Background = "#FFFF0000", BackgroundOpacity = .9 };
        foreach (var kind in Enum.GetValues<DesktopWidgetKind>())
        foreach (var theme in Enum.GetValues<WidgetColorTheme>())
        {
            var widget = new DesktopWidget { Kind = kind, ColorTheme = theme, BackgroundMaterial = WidgetBackgroundMaterial.Acrylic };
            var surface = WidgetAppearance.Resolve(widget, inherited, true, widgetAcrylic: settings);
            Assert.Equal(background, surface.Background);
            Assert.Equal(.23, surface.Opacity);
            Assert.True(surface.Acrylic);
            Assert.Null(surface.GradientEnd);
            widget.ColorTheme = WidgetColorTheme.Dark;
            var otherTheme = WidgetAppearance.Resolve(widget, inherited, true, widgetAcrylic: settings);
            Assert.Equal(surface, otherTheme);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AcrylicAndLegacyMaterialKeepTheSameBoxDesign(bool followBox)
    {
        var box = new BoxAppearance { Background = "#FF123456", Accent = "#FFABCDEF", BackgroundOpacity = .76 };
        var settings = new WidgetAcrylicSettings { FollowBoxAppearance = followBox, Background = "#FF20242C", BackgroundOpacity = .23 };
        foreach (var kind in Enum.GetValues<DesktopWidgetKind>())
        {
            var widget = new DesktopWidget { Kind = kind, BackgroundMaterial = WidgetBackgroundMaterial.Inherit };
            var legacy = WidgetAppearance.Resolve(widget, box, true, widgetAcrylic: settings);
            widget.BackgroundMaterial = WidgetBackgroundMaterial.Acrylic;
            var surface = WidgetAppearance.Resolve(widget, box, true, widgetAcrylic: settings);
            Assert.Equal(legacy, surface);
            Assert.Equal(box.Accent, surface.Accent);
            Assert.Equal(followBox ? box.Background : settings.Background, surface.Background);
            Assert.Equal(followBox ? .76 : .23, surface.Opacity);
        }
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public void AcrylicHostFollowsTheSelectedSource(bool followBox, bool boxAcrylic, bool expected)
    {
        var state = new CrabDeskState();
        state.Settings.Appearance.UseAcrylicBoxes = boxAcrylic;
        state.Settings.Appearance.WidgetAcrylic.FollowBoxAppearance = followBox;
        foreach (var kind in Enum.GetValues<DesktopWidgetKind>())
        foreach (var material in new[] { WidgetBackgroundMaterial.Inherit, WidgetBackgroundMaterial.Acrylic })
        {
            var widget = new DesktopWidget { Kind = kind, BackgroundMaterial = material };
            Assert.Equal(expected, WidgetAppearance.UsesAcrylic(widget, boxAcrylic, state.Settings.Appearance.WidgetAcrylic));
            state.Widgets.Clear();
            state.Widgets.Add(widget);
            Assert.Equal(expected, WidgetAppearance.RequiresAcrylic(state));
        }
    }

    [Fact]
    public void InheritedWidgetKeepsBoxSettings()
    {
        var box = new BoxAppearance { Background = "#FF123456", Accent = "#FFABCDEF", BackgroundOpacity = .76 };
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Note };
        var surface = WidgetAppearance.Resolve(widget, box, true,
            widgetAcrylic: new() { Background = "#FFFFFFFF", BackgroundOpacity = .1 });
        Assert.Equal(new WidgetSurfaceAppearance(box.Background, box.Accent, null, .76, true), surface);
    }

    [Theory]
    [InlineData("{}", "#FF20242C", .45, 50)]
    [InlineData("""{"WidgetAcrylic":null}""", "#FF20242C", .45, 50)]
    [InlineData("""{"WidgetAcrylic":{"Background":"#80123456","BackgroundOpacity":-1,"BlurStrength":200}}""", "#FF123456", 0, 100)]
    [InlineData("""{"WidgetAcrylic":{"Background":"invalid","BackgroundOpacity":2,"BlurStrength":-1}}""", "#FF20242C", 1, 0)]
    public async Task LoadingRestoresAndNormalizesSharedSettings(string appearance, string background, double opacity, double blur)
    {
        var root = Path.Combine(Path.GetTempPath(), "CrabDesk.WidgetAcrylicTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "config.json"),
            "{\"SchemaVersion\":24,\"Settings\":{\"Appearance\":" + appearance + "}}");
        var settings = (await new JsonLayoutStore(root).LoadAsync()).Settings.Appearance.WidgetAcrylic;
        Assert.Equal(background, settings.Background);
        Assert.Equal(opacity, settings.BackgroundOpacity);
        Assert.Equal(blur, settings.BlurStrength);
        Assert.True(settings.FollowBoxAppearance);
    }

    [Fact]
    public async Task SharedSettingsSurviveConfigurationAndBackupRoundTrips()
    {
        var root = Path.Combine(Path.GetTempPath(), "CrabDesk.WidgetAcrylicTests", Guid.NewGuid().ToString("N"));
        var state = new CrabDeskState();
        state.Settings.Appearance.WidgetAcrylic = new() { FollowBoxAppearance = false, Background = "#FF406080", BackgroundOpacity = .23, BlurStrength = 81 };
        var store = new JsonLayoutStore(root);
        await store.SaveAsync(state);
        var loaded = await store.LoadAsync();
        var backups = new JsonBackupService(Path.Combine(root, "backups"));
        var info = await backups.CreateAsync(loaded);
        var restored = await backups.LoadAsync(info.Path);
        foreach (var settings in new[] { loaded.Settings.Appearance.WidgetAcrylic, restored.Settings.Appearance.WidgetAcrylic })
        {
            Assert.Equal("#FF406080", settings.Background);
            Assert.Equal(.23, settings.BackgroundOpacity);
            Assert.Equal(81, settings.BlurStrength);
            Assert.False(settings.FollowBoxAppearance);
        }
    }

    [Fact]
    public void NonFiniteValuesUseDefaults()
    {
        var settings = new WidgetAcrylicSettings { BackgroundOpacity = double.NaN, BlurStrength = double.PositiveInfinity };
        settings.Normalize();
        Assert.Equal(.45, settings.BackgroundOpacity);
        Assert.Equal(50, settings.BlurStrength);
    }
}
