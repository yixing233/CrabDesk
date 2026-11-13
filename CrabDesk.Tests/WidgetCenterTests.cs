using System.Text.Json;
using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class WidgetCenterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CrabDesk.WidgetCenterTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void DefaultsKeepWidgetsOnDesktopAndCenterDisabled()
    {
        var state = JsonLayoutStore.CreateDefaultState();
        var widget = new DesktopWidget();
        Assert.Equal(25, state.SchemaVersion);
        Assert.Equal(0, (int)WidgetPlacement.Desktop);
        Assert.Equal(1, (int)WidgetPlacement.Center);
        Assert.Equal(WidgetPlacement.Desktop, widget.Placement);
        Assert.Equal(0, widget.CenterOrder);
        Assert.False(state.Settings.WidgetCenter.Enabled);
        Assert.Equal(ApplicationThemeMode.System, state.Settings.WidgetCenter.ThemeMode);
        Assert.Equal(840, state.Settings.WidgetCenter.Width);
        Assert.Equal(680, state.Settings.WidgetCenter.Height);
        Assert.Empty(state.Settings.WidgetCenter.MonitorId);
        Assert.Equal(0, state.Settings.WidgetCenter.X);
        Assert.Equal(0, state.Settings.WidgetCenter.Y);
        Assert.False(state.Settings.WidgetCenter.HasPosition);
        Assert.False(state.Settings.Hotkeys.WidgetCenter.Enabled);
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, state.Settings.Hotkeys.WidgetCenter.Modifiers);
        Assert.Equal(HotkeyKey.W, state.Settings.Hotkeys.WidgetCenter.Key);
        Assert.Equal(0, (int)HotkeyAction.ShowDesktop);
        Assert.Equal(1, (int)HotkeyAction.OrganizeDesktop);
        Assert.Equal(2, (int)HotkeyAction.WidgetCenter);
        Assert.NotSame(state.Settings.WidgetCenter, new AppSettings().WidgetCenter);
        Assert.NotSame(state.Settings.Hotkeys.WidgetCenter, new HotkeySettings().WidgetCenter);
    }

    [Theory]
    [InlineData(ApplicationThemeMode.System)]
    [InlineData(ApplicationThemeMode.Light)]
    [InlineData(ApplicationThemeMode.Dark)]
    public async Task CenterThemePersistsIndependentlyOfTheApplicationTheme(ApplicationThemeMode mode)
    {
        var state = new CrabDeskState();
        state.Settings.ThemeMode = ApplicationThemeMode.Light;
        state.Settings.WidgetCenter.ThemeMode = mode;
        var store = new JsonLayoutStore(_root);
        await store.SaveAsync(state);
        var restored = await store.LoadAsync();
        Assert.Equal(mode, restored.Settings.WidgetCenter.ThemeMode);
        Assert.Equal(ApplicationThemeMode.Light, restored.Settings.ThemeMode);
        Assert.False(restored.Settings.WidgetCenter.Enabled);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    public void UnknownCenterThemeFallsBackToSystem(int mode)
    {
        var settings = new WidgetCenterSettings { ThemeMode = (ApplicationThemeMode)mode };
        Assert.Equal(ApplicationThemeMode.System, WidgetCenterSettings.Normalize(settings).ThemeMode);
        Assert.Equal(ApplicationThemeMode.System,
            JsonSerializer.Deserialize<WidgetCenterSettings>("{}")!.ThemeMode);
    }

    [Theory]
    [InlineData(WidgetColorTheme.Light)]
    [InlineData(WidgetColorTheme.Dark)]
    public void CenterThemeOverridesEverySavedWidgetPaletteWithoutChangingDesktopAppearance(WidgetColorTheme theme)
    {
        var inherited = new BoxAppearance { Background = "#FF123456", BackgroundOpacity = .3 };
        foreach (var kind in Enum.GetValues<DesktopWidgetKind>())
        foreach (var material in Enum.GetValues<WidgetBackgroundMaterial>())
        foreach (var savedTheme in Enum.GetValues<WidgetColorTheme>())
        {
            var widget = new DesktopWidget
            {
                Kind = kind, Placement = WidgetPlacement.Center, BackgroundMaterial = material,
                ColorTheme = savedTheme, LyricsTransparentBackground = true
            };
            var saved = JsonSerializer.Serialize(widget);
            var desktop = WidgetAppearance.Resolve(widget, inherited, true);
            var surface = WidgetAppearance.Resolve(widget, inherited, true, forceSolid: true, colorTheme: theme);
            var expected = WidgetAppearance.Resolve(new DesktopWidget
            {
                Kind = kind, BackgroundMaterial = WidgetBackgroundMaterial.Solid, ColorTheme = theme
            }, inherited, false, forceSolid: true);
            Assert.Equal(expected, surface);
            Assert.False(surface.Acrylic);
            Assert.Equal(1, surface.Opacity);
            Assert.Null(surface.GradientEnd);
            Assert.Equal(saved, JsonSerializer.Serialize(widget));
            Assert.Equal(desktop, WidgetAppearance.Resolve(widget, inherited, true));
        }
    }

    [Theory]
    [InlineData(-1, 0, double.NaN, double.PositiveInfinity)]
    [InlineData(double.NaN, double.NegativeInfinity, double.NegativeInfinity, double.NaN)]
    public void InvalidSettingsNormalizeToFiniteDefaults(double width, double height, double x, double y)
    {
        var settings = new WidgetCenterSettings
        {
            Enabled = true, Width = width, Height = height, MonitorId = null!, X = x, Y = y, HasPosition = true
        };
        Assert.Same(settings, WidgetCenterSettings.Normalize(settings));
        Assert.True(settings.Enabled);
        Assert.Equal(840, settings.Width);
        Assert.Equal(680, settings.Height);
        Assert.Empty(settings.MonitorId);
        Assert.Equal(0, settings.X);
        Assert.Equal(0, settings.Y);
        Assert.False(settings.HasPosition);
        WidgetCenterSettings.Normalize(settings);
        Assert.Equal(840, settings.Width);
        Assert.Equal(680, settings.Height);
    }

    [Fact]
    public void ValidSettingsRetainNegativeMonitorCoordinatesAndNormalizeNull()
    {
        var settings = new WidgetCenterSettings
        {
            Width = 920.5, Height = 740.5, MonitorId = " display-left ", X = -960.5, Y = -40, HasPosition = true
        };
        WidgetCenterSettings.Normalize(settings);
        Assert.Equal(920.5, settings.Width);
        Assert.Equal(740.5, settings.Height);
        Assert.Equal("display-left", settings.MonitorId);
        Assert.Equal(-960.5, settings.X);
        Assert.Equal(-40, settings.Y);
        Assert.True(settings.HasPosition);
        var defaults = WidgetCenterSettings.Normalize(null);
        Assert.False(defaults.Enabled);
        Assert.Equal(840, defaults.Width);
        Assert.Equal(680, defaults.Height);
    }

    [Fact]
    public async Task SchemaTwentyFourMigratesWithoutChangingDesktopPlacementOrTranslationProfile()
    {
        var store = new JsonLayoutStore(_root);
        await File.WriteAllTextAsync(store.StatePath, """
            {"SchemaVersion":24,"Settings":{"WidgetTranslation":{"BaseUrl":"https://translator.test/v1","Model":"translator"}},
             "Widgets":[{"Kind":2,"Size":1,"MonitorId":"desktop-monitor","Bounds":{"X":48,"Y":72,"Width":180,"Height":180}}]}
            """);
        var state = await store.LoadAsync();
        var widget = Assert.Single(state.Widgets);
        Assert.Equal(25, state.SchemaVersion);
        Assert.Equal(WidgetPlacement.Desktop, widget.Placement);
        Assert.Equal(0, widget.CenterOrder);
        Assert.Equal("desktop-monitor", widget.MonitorId);
        Assert.Equal(new LayoutRect(48, 72, 180, 180), widget.Bounds);
        Assert.False(state.Settings.WidgetCenter.Enabled);
        Assert.False(state.Settings.Hotkeys.WidgetCenter.Enabled);
        Assert.Equal(HotkeyKey.W, state.Settings.Hotkeys.WidgetCenter.Key);
        Assert.Equal("translator", state.Settings.WidgetTranslation.Model);
        Assert.Equal("https://translator.test/v1", state.Settings.WidgetTranslation.BaseUrl);
        await store.SaveAsync(state);
        Assert.Equal(WidgetPlacement.Desktop, Assert.Single((await store.LoadAsync()).Widgets).Placement);
    }

    [Theory]
    [InlineData(-1, -4, -1, WidgetPlacement.Desktop, 0, DesktopWidgetSize.Large)]
    [InlineData(99, int.MinValue, 99, WidgetPlacement.Desktop, 0, DesktopWidgetSize.Large)]
    [InlineData(1, int.MaxValue, 1, WidgetPlacement.Center, int.MaxValue, DesktopWidgetSize.Medium)]
    public async Task InvalidOwnershipOrderSizeAndHotkeyNormalizeOnLoad(int placement, int order, int size,
        WidgetPlacement expectedPlacement, int expectedOrder, DesktopWidgetSize expectedSize)
    {
        var store = new JsonLayoutStore(_root);
        await File.WriteAllTextAsync(store.StatePath, $$"""
            {"SchemaVersion":25,"Settings":{"WidgetCenter":null,"Hotkeys":{"WidgetCenter":{"Enabled":true,"Modifiers":16,"Key":999} } },
             "Widgets":[{"Kind":2,"Placement":{{placement}},"CenterOrder":{{order}},"Size":{{size}},"MonitorId":"saved-desktop",
                         "Bounds":{"X":48,"Y":72,"Width":180,"Height":180} }]}
            """);
        var state = await store.LoadAsync();
        var widget = Assert.Single(state.Widgets);
        Assert.Equal(expectedPlacement, widget.Placement);
        Assert.Equal(expectedOrder, widget.CenterOrder);
        Assert.Equal(expectedSize, widget.Size);
        Assert.Equal("saved-desktop", widget.MonitorId);
        Assert.Equal(48, widget.Bounds.X);
        Assert.Equal(72, widget.Bounds.Y);
        Assert.False(state.Settings.WidgetCenter.Enabled);
        Assert.True(state.Settings.Hotkeys.WidgetCenter.Enabled);
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, state.Settings.Hotkeys.WidgetCenter.Modifiers);
        Assert.Equal(HotkeyKey.W, state.Settings.Hotkeys.WidgetCenter.Key);
    }

    [Fact]
    public async Task NullHotkeysAndInvalidCenterDimensionsNormalizeBeforeSerialization()
    {
        var state = new CrabDeskState();
        state.Settings.WidgetCenter = new()
        {
            Width = double.NaN, Height = double.PositiveInfinity, X = double.NaN, Y = double.NegativeInfinity, HasPosition = true
        };
        state.Settings.Hotkeys = null!;
        var store = new JsonLayoutStore(_root);
        await store.SaveAsync(state);
        var restored = await store.LoadAsync();
        Assert.Equal(840, restored.Settings.WidgetCenter.Width);
        Assert.Equal(680, restored.Settings.WidgetCenter.Height);
        Assert.False(restored.Settings.WidgetCenter.HasPosition);
        Assert.False(restored.Settings.Hotkeys.WidgetCenter.Enabled);
        restored.Settings.Hotkeys.WidgetCenter = null!;
        await store.SaveAsync(restored);
        Assert.Equal(HotkeyKey.W, (await store.LoadAsync()).Settings.Hotkeys.WidgetCenter.Key);
    }

    [Fact]
    public async Task CenterSettingsOwnershipAndDesktopBoundsSurviveJsonRoundTrip()
    {
        var center = new DesktopWidget { Kind = DesktopWidgetKind.Note, Placement = WidgetPlacement.Center, CenterOrder = 7,
            Size = DesktopWidgetSize.Custom, MonitorId = "desktop-left", Bounds = new(48, 72, 320.5, 240.5), Text = "中心便签" };
        var desktop = new DesktopWidget { Kind = DesktopWidgetKind.Clock, MonitorId = "desktop-right", Bounds = new(80, 96, 180, 180) };
        var state = new CrabDeskState { Widgets = [center, desktop] };
        state.Settings.WidgetCenter = new() { Enabled = true, Width = 920, Height = 740,
            MonitorId = "center-monitor", X = -900, Y = 50, HasPosition = true };
        state.Settings.Hotkeys.WidgetCenter = new() { Enabled = true, Modifiers = HotkeyModifiers.Windows | HotkeyModifiers.Shift, Key = HotkeyKey.F8 };
        var store = new JsonLayoutStore(_root);
        await store.SaveAsync(state);
        var restored = await store.LoadAsync();
        Assert.Equal(25, restored.SchemaVersion);
        Assert.Equal(WidgetPlacement.Center, restored.Widgets[0].Placement);
        Assert.Equal(7, restored.Widgets[0].CenterOrder);
        Assert.Equal(center.Id, restored.Widgets[0].Id);
        Assert.Equal(center.Bounds, restored.Widgets[0].Bounds);
        Assert.Equal(center.MonitorId, restored.Widgets[0].MonitorId);
        Assert.Equal(center.Text, restored.Widgets[0].Text);
        Assert.Equal(WidgetPlacement.Desktop, restored.Widgets[1].Placement);
        Assert.Equal(desktop.Bounds, restored.Widgets[1].Bounds);
        Assert.True(restored.Settings.WidgetCenter.Enabled);
        Assert.Equal(920, restored.Settings.WidgetCenter.Width);
        Assert.Equal(740, restored.Settings.WidgetCenter.Height);
        Assert.Equal("center-monitor", restored.Settings.WidgetCenter.MonitorId);
        Assert.Equal(-900, restored.Settings.WidgetCenter.X);
        Assert.Equal(50, restored.Settings.WidgetCenter.Y);
        Assert.True(restored.Settings.WidgetCenter.HasPosition);
        Assert.True(restored.Settings.Hotkeys.WidgetCenter.Enabled);
        Assert.Equal(HotkeyModifiers.Windows | HotkeyModifiers.Shift, restored.Settings.Hotkeys.WidgetCenter.Modifiers);
        Assert.Equal(HotkeyKey.F8, restored.Settings.Hotkeys.WidgetCenter.Key);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(store.StatePath));
        Assert.Equal(1, json.RootElement.GetProperty("Widgets")[0].GetProperty("Placement").GetInt32());
        Assert.Equal(7, json.RootElement.GetProperty("Widgets")[0].GetProperty("CenterOrder").GetInt32());
        Assert.True(json.RootElement.GetProperty("Settings").GetProperty("WidgetCenter").GetProperty("Enabled").GetBoolean());
    }

    [Fact]
    public void ArrangeFlowsRowsInOrderAndNeverChangesDesktopBounds()
    {
        var first = new DesktopWidget { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Kind = DesktopWidgetKind.Note, Size = DesktopWidgetSize.Custom, Placement = WidgetPlacement.Center, CenterOrder = 1,
            MonitorId = "desktop-left", Bounds = new(50, 60, 180, 240) };
        var second = new DesktopWidget { Id = Guid.Parse("00000000-0000-0000-0000-000000000002"),
            Kind = DesktopWidgetKind.Clock, Size = DesktopWidgetSize.Medium, Placement = WidgetPlacement.Center, CenterOrder = 1,
            MonitorId = "desktop-right", Bounds = new(70, 80, 999, 999) };
        var third = new DesktopWidget { Kind = DesktopWidgetKind.Note, Size = DesktopWidgetSize.Large,
            Placement = WidgetPlacement.Center, CenterOrder = 2, Bounds = new(90, 100, 372, 180) };
        var desktop = new DesktopWidget();
        DesktopWidget[] widgets = [third, second, desktop, first];
        var originals = widgets.ToDictionary(widget => widget.Id, widget => (widget.Bounds, widget.MonitorId));
        var arranged = WidgetCenterLayout.Arrange(widgets, 376);
        Assert.IsType<Dictionary<Guid, LayoutRect>>(arranged.Bounds);
        Assert.Equal(3, arranged.Bounds.Count);
        Assert.DoesNotContain(desktop.Id, arranged.Bounds.Keys);
        Assert.Equal(new LayoutRect(0, 0, 180, 240), arranged.Bounds[first.Id]);
        Assert.Equal(new LayoutRect(196, 0, 180, 180), arranged.Bounds[second.Id]);
        Assert.Equal(new LayoutRect(0, 256, 372, 180), arranged.Bounds[third.Id]);
        Assert.Equal(436, arranged.Height);
        var reordered = WidgetCenterLayout.Arrange(widgets.Reverse().ToArray(), 376);
        Assert.Equal(arranged.Height, reordered.Height);
        foreach (var widget in widgets)
        {
            Assert.Equal(originals[widget.Id].Bounds, widget.Bounds);
            Assert.Equal(originals[widget.Id].MonitorId, widget.MonitorId);
            if (widget.Placement == WidgetPlacement.Center) Assert.Equal(arranged.Bounds[widget.Id], reordered.Bounds[widget.Id]);
        }
        arranged.Bounds.Clear();
        Assert.Equal(3, reordered.Bounds.Count);
    }

    [Fact]
    public void ArrangeCapsWideCardsAndUsesPresetAndCustomDimensions()
    {
        var wide = new DesktopWidget { Kind = DesktopWidgetKind.Note, Size = DesktopWidgetSize.Custom,
            Placement = WidgetPlacement.Center, CenterOrder = 0, Bounds = new(48, 72, 900, 300) };
        var panel = new DesktopWidget { Kind = DesktopWidgetKind.ControlCenter, Size = DesktopWidgetSize.Panel,
            Placement = WidgetPlacement.Center, CenterOrder = 1 };
        var result = WidgetCenterLayout.Arrange([panel, wide], 300, 12);
        var dimensions = DesktopWidgets.Dimensions(panel, panel.Size);
        Assert.Equal(new LayoutRect(0, 0, 300, 300), result.Bounds[wide.Id]);
        Assert.Equal(new LayoutRect(0, 312, Math.Min(300, dimensions.Width), dimensions.Height), result.Bounds[panel.Id]);
        Assert.Equal(312 + dimensions.Height, result.Height);
        Assert.Equal(new LayoutRect(48, 72, 900, 300), wide.Bounds);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void ArrangeReturnsEmptyForUnavailableWidth(double width)
    {
        var result = WidgetCenterLayout.Arrange([new() { Placement = WidgetPlacement.Center }], width);
        Assert.Empty(result.Bounds);
        Assert.Equal(0, result.Height);
    }

    [Theory]
    [InlineData(-8, 180)] [InlineData(double.NaN, 196)] [InlineData(double.PositiveInfinity, 196)]
    public void ArrangeNormalizesGapAndEmptyContentHasNoHeight(double gap, double expectedX)
    {
        var first = new DesktopWidget { Placement = WidgetPlacement.Center, CenterOrder = 0 };
        var second = new DesktopWidget { Placement = WidgetPlacement.Center, CenterOrder = 1 };
        var result = WidgetCenterLayout.Arrange([first, second], 400, gap);
        Assert.Equal(expectedX, result.Bounds[second.Id].X);
        Assert.Equal(180, result.Height);
        var empty = WidgetCenterLayout.Arrange([], 400);
        Assert.Empty(empty.Bounds);
        Assert.Equal(0, empty.Height);
    }

    [Fact]
    public void LayoutResultSupportsJsonSerializationWithoutPersistingIntoState()
    {
        var widget = new DesktopWidget { Placement = WidgetPlacement.Center, Bounds = new(48, 72, 180, 180) };
        var result = WidgetCenterLayout.Arrange([widget], 840);
        var restored = JsonSerializer.Deserialize<WidgetCenterLayoutResult>(JsonSerializer.Serialize(result))!;
        Assert.Equal(result.Height, restored.Height);
        Assert.Equal(result.Bounds[widget.Id], restored.Bounds[widget.Id]);
        var state = JsonSerializer.Deserialize<CrabDeskState>(JsonSerializer.Serialize(new CrabDeskState { Widgets = [widget] }))!;
        Assert.Equal(widget.Bounds, Assert.Single(state.Widgets).Bounds);
        Assert.Equal(WidgetPlacement.Center, state.Widgets[0].Placement);
    }

    [Fact]
    public void CenterAcrylicDoesNotRequireDesktopAcrylicButDesktopWidgetsAndBoxesStillDo()
    {
        var widget = new DesktopWidget { Placement = WidgetPlacement.Center, BackgroundMaterial = WidgetBackgroundMaterial.Acrylic };
        var state = new CrabDeskState { Widgets = [widget] };
        state.Settings.Appearance.WidgetAcrylic.FollowBoxAppearance = false;
        Assert.True(WidgetAppearance.UsesAcrylic(widget, false, state.Settings.Appearance.WidgetAcrylic));
        Assert.False(WidgetAppearance.RequiresAcrylic(state));
        widget.Placement = WidgetPlacement.Desktop;
        Assert.True(WidgetAppearance.RequiresAcrylic(state));
        widget.Placement = WidgetPlacement.Center;
        state.Settings.Appearance.UseAcrylicBoxes = true;
        Assert.True(WidgetAppearance.RequiresAcrylic(state));
    }

    [Theory]
    [InlineData(DesktopWidgetKind.Clock)] [InlineData(DesktopWidgetKind.Calendar)]
    [InlineData(DesktopWidgetKind.Note)] [InlineData(DesktopWidgetKind.Todo)]
    [InlineData(DesktopWidgetKind.Translation)] [InlineData(DesktopWidgetKind.Weather)]
    [InlineData(DesktopWidgetKind.Performance)] [InlineData(DesktopWidgetKind.Network)]
    [InlineData(DesktopWidgetKind.ControlCenter)] [InlineData(DesktopWidgetKind.Lyrics)]
    [InlineData(DesktopWidgetKind.FileShelf)]
    public void ForceSolidUsesOpaquePaletteForEveryCenterWidgetWithoutChangingModel(DesktopWidgetKind kind)
    {
        var inherited = new BoxAppearance { Background = "#80123456", Accent = "#FFABCDEF", BackgroundOpacity = .2 };
        foreach (var material in Enum.GetValues<WidgetBackgroundMaterial>())
        foreach (var theme in Enum.GetValues<WidgetColorTheme>())
        foreach (var followBox in new[] { true, false })
        {
            var widget = new DesktopWidget
            {
                Kind = kind, Placement = WidgetPlacement.Center, BackgroundMaterial = material,
                ColorTheme = theme, LyricsTransparentBackground = true, Bounds = new(48, 72, 180, 180)
            };
            var settings = new WidgetAcrylicSettings
            {
                FollowBoxAppearance = followBox, Background = "#FF123456", BackgroundOpacity = .1, BlurStrength = 100
            };
            var originalWidget = JsonSerializer.Serialize(widget);
            var originalInherited = JsonSerializer.Serialize(inherited);
            var originalSettings = JsonSerializer.Serialize(settings);
            var surface = WidgetAppearance.Resolve(widget, inherited, true, widgetAcrylic: settings, forceSolid: true);
            Assert.False(surface.Acrylic);
            Assert.Equal(1, surface.Opacity);
            Assert.Null(surface.GradientEnd);
            Assert.Contains(surface.Background, new[] { "#FFFFFFFF", "#FF1C1C1E", "#FF000000" });
            if (kind != DesktopWidgetKind.Lyrics)
            {
                var solid = new DesktopWidget { Kind = kind, BackgroundMaterial = WidgetBackgroundMaterial.Solid, ColorTheme = theme };
                Assert.Equal(WidgetAppearance.Resolve(solid, inherited, false), surface);
            }
            else
            {
                Assert.Equal(theme == WidgetColorTheme.Dark ? "#FF1C1C1E" : "#FFFFFFFF", surface.Background);
                Assert.Equal(theme == WidgetColorTheme.Dark ? "#FF91B9EF" : "#FF356BB3", surface.Accent);
            }
            Assert.Equal(surface, WidgetAppearance.Resolve(widget, inherited, false, widgetAcrylic: settings, forceSolid: true));
            Assert.Equal(originalWidget, JsonSerializer.Serialize(widget));
            Assert.Equal(originalInherited, JsonSerializer.Serialize(inherited));
            Assert.Equal(originalSettings, JsonSerializer.Serialize(settings));
        }
    }

    [Theory]
    [InlineData(WidgetBackgroundMaterial.Inherit, false)] [InlineData(WidgetBackgroundMaterial.Inherit, true)]
    [InlineData(WidgetBackgroundMaterial.Solid, false)] [InlineData(WidgetBackgroundMaterial.Solid, true)]
    [InlineData(WidgetBackgroundMaterial.Acrylic, false)] [InlineData(WidgetBackgroundMaterial.Acrylic, true)]
    public void ForceSolidLyricsDoesNotChangeSavedBackgroundOrSubsequentDesktopAppearance(WidgetBackgroundMaterial material, bool transparent)
    {
        var widget = new DesktopWidget
        {
            Kind = DesktopWidgetKind.Lyrics, Placement = WidgetPlacement.Center,
            BackgroundMaterial = material, LyricsTransparentBackground = transparent, ColorTheme = WidgetColorTheme.Dark
        };
        var inherited = new BoxAppearance { BackgroundOpacity = .3 };
        var desktop = WidgetAppearance.Resolve(widget, inherited, true);
        var forced = WidgetAppearance.Resolve(widget, inherited, true, forceSolid: true);
        Assert.Equal(new WidgetSurfaceAppearance("#FF1C1C1E", "#FF91B9EF", null, 1, false), forced);
        Assert.Equal(material, widget.BackgroundMaterial);
        Assert.Equal(transparent, widget.LyricsTransparentBackground);
        Assert.Equal(desktop, WidgetAppearance.Resolve(widget, inherited, true));
        Assert.Equal(transparent ? 0 : .3, desktop.Opacity);
        Assert.Equal(!transparent, desktop.Acrylic);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void ForceSolidDefaultAndExplicitFalsePreserveExistingAppearance(bool followBox)
    {
        var inherited = new BoxAppearance { BackgroundOpacity = .3 };
        var settings = new WidgetAcrylicSettings { FollowBoxAppearance = followBox, BackgroundOpacity = .2 };
        foreach (var kind in Enum.GetValues<DesktopWidgetKind>())
        foreach (var material in Enum.GetValues<WidgetBackgroundMaterial>())
        {
            var widget = new DesktopWidget { Kind = kind, BackgroundMaterial = material };
            var original = JsonSerializer.Serialize(widget);
            var normal = WidgetAppearance.Resolve(widget, inherited, true, widgetAcrylic: settings);
            Assert.Equal(normal, WidgetAppearance.Resolve(widget, inherited, true, widgetAcrylic: settings, forceSolid: false));
            Assert.Equal(kind == DesktopWidgetKind.Lyrics || material != WidgetBackgroundMaterial.Solid, normal.Acrylic);
            Assert.Equal(original, JsonSerializer.Serialize(widget));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
