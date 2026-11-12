using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class BackdropAppearanceSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CrabDesk.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(-1d, 0d)]
    [InlineData(0d, 0d)]
    [InlineData(16d, 16d)]
    [InlineData(30d, 20d)]
    [InlineData(double.NaN, 8d)]
    [InlineData(double.PositiveInfinity, 8d)]
    public void WidgetCornerRadiusHasItsOwnValidatedValue(double value, double expected)
    {
        var settings = new GlobalAppearanceSettings { CornerRadius = 3, WidgetCornerRadius = value };
        Assert.Equal(expected, settings.ResolveWidgetCornerRadius());
        Assert.Equal(3, settings.CornerRadius);
    }

    [Fact]
    public async Task LegacyWidgetCornersArePreservedAndThenSavedIndependently()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "config.json"), """
            {"SchemaVersion":24,"Settings":{"Appearance":{"CornerRadius":14}}}
            """);
        var store = new JsonLayoutStore(_root);
        var state = await store.LoadAsync();
        Assert.Equal(14, state.Settings.Appearance.WidgetCornerRadius);
        state.Settings.Appearance.CornerRadius = 2;
        Assert.Equal(14, state.Settings.Appearance.ResolveWidgetCornerRadius());
        state.Settings.Appearance.WidgetCornerRadius = 0;
        await store.SaveAsync(state);
        var loaded = await store.LoadAsync();
        Assert.Equal(0, loaded.Settings.Appearance.ResolveWidgetCornerRadius());
        Assert.Equal(2, loaded.Settings.Appearance.CornerRadius);
        var backups = new JsonBackupService(Path.Combine(_root, "backups"));
        var backup = await backups.CreateAsync(loaded);
        Assert.Equal(0, (await backups.LoadAsync(backup.Path)).Settings.Appearance.ResolveWidgetCornerRadius());
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(.12d)]
    [InlineData(.72d)]
    [InlineData(1d)]
    public void ExplicitOpacityIsIndependentOfTheAcrylicSwitch(double value)
    {
        var appearance = new BoxAppearance { BackgroundOpacity = value };
        Assert.Equal(value, appearance.ResolveBackgroundOpacity(false));
        Assert.Equal(value, appearance.ResolveBackgroundOpacity(true));
    }

    [Theory]
    [InlineData(-1d, 0d)]
    [InlineData(2d, 1d)]
    public void ExplicitOpacityClampsToTransparentAndOpaqueEndpoints(double value, double expected)
    {
        var appearance = new BoxAppearance { BackgroundOpacity = value };
        Assert.Equal(expected, appearance.ResolveBackgroundOpacity(true));
    }

    [Theory]
    [InlineData(false, .5d)]
    [InlineData(true, .36d)]
    public async Task LoadingLegacyOpacityPreservesItsAppearanceWithoutRepeatedAttenuation(bool acrylic, double expected)
    {
        Directory.CreateDirectory(_root);
        var legacy = System.Text.Json.JsonSerializer.Serialize(new
        {
            SchemaVersion = 24,
            Settings = new { Appearance = new { UseAcrylicBoxes = acrylic }, DefaultBoxAppearance = new { Opacity = .5 } },
            Boxes = new[] { new { Appearance = new { Opacity = .5 } } }
        });
        await File.WriteAllTextAsync(Path.Combine(_root, "config.json"), legacy);
        var store = new JsonLayoutStore(_root);
        var state = await store.LoadAsync();
        Assert.Equal(expected, state.Boxes[0].Appearance.BackgroundOpacity);
        Assert.Equal(expected, state.Settings.DefaultBoxAppearance.BackgroundOpacity);
        Assert.Equal(50, state.Settings.Appearance.AcrylicBlurStrength);
        await store.SaveAsync(state);
        state = await store.LoadAsync();
        Assert.Equal(expected, state.Boxes[0].Appearance.ResolveBackgroundOpacity(acrylic));
        Assert.Equal(expected, state.Settings.DefaultBoxAppearance.ResolveBackgroundOpacity(acrylic));
    }

    [Theory]
    [InlineData(0d, 0d)]
    [InlineData(.2d, 25d)]
    [InlineData(1d, 100d)]
    public async Task SurfaceParametersSurviveSavingAndLoading(double opacity, double blur)
    {
        var state = new CrabDeskState();
        state.Settings.Appearance.UseAcrylicBoxes = true;
        state.Settings.Appearance.AcrylicBlurStrength = blur;
        state.Boxes.Add(new DesktopBox { Appearance = new BoxAppearance { Background = "#FF365A71", BackgroundOpacity = opacity } });
        var store = new JsonLayoutStore(_root);
        await store.SaveAsync(state);
        var loaded = await store.LoadAsync();
        Assert.Equal("#FF365A71", loaded.Boxes[0].Appearance.Background);
        Assert.Equal(opacity, loaded.Boxes[0].Appearance.ResolveBackgroundOpacity(true));
        Assert.Equal(blur, loaded.Settings.Appearance.AcrylicBlurStrength);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
