using System.Collections;
using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Xml.Linq;
using CrabDesk.Core;
using CrabDesk.Runtime;
using Windows.UI.Composition;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class BackdropControlsIntegrationTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void WidgetCornerRadiusUpdatesAndSavesIndependentlyOfBoxes()
    {
        var runtime = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        runtime.State.Settings.Appearance.CornerRadius = 3;
        runtime.SetWidgetCornerRadius(16);
        Assert.Equal(16, runtime.State.Settings.Appearance.ResolveWidgetCornerRadius());
        Assert.Equal(3, runtime.State.Settings.Appearance.CornerRadius);
        Assert.NotNull(Get(timer, "_timer"));
        runtime.SetCornerRadius(0);
        Assert.Equal(16, runtime.State.Settings.Appearance.ResolveWidgetCornerRadius());
        runtime.SetWidgetCornerRadius(-1);
        Assert.Equal(0, runtime.State.Settings.Appearance.ResolveWidgetCornerRadius());
        runtime.SetWidgetCornerRadius(30);
        Assert.Equal(20, runtime.State.Settings.Appearance.ResolveWidgetCornerRadius());
    }

    [Theory]
    [InlineData(1d)]
    [InlineData(1.5d)]
    public void SharedWidgetBlurUpdatesIndependentlyOfBoxBlur(double scale) => OnSta(() =>
    {
        using var desktop = new System.Windows.Forms.Form();
        var monitor = Monitor(scale);
        using var host = new DesktopAcrylicHost(desktop.Handle, [monitor], CrabDesk.WinUI.Services.AcrylicBlurEffects.CreateFactory);
        host.InitializeComposition();
        using var frame = DesktopLayerBitmapFactory.Create(200, 160);
        var boxId = Guid.NewGuid();
        var widgetId = Guid.NewGuid();
        var widgetRegion = new AcrylicBoxRegion(widgetId, new(100, 0, 100, 160), 8, (float)scale, BlurStrength: 75);
        host.SetBlurStrength(25);
        host.PresentForeground(monitor.Id, frame, Point.Empty,
            [new(boxId, new(0, 0, 100, 160), 8, (float)scale), widgetRegion]);
        var regions = (IDictionary)Get(host, "_boxes")!;
        var widget = regions[widgetId]!;
        var box = regions[boxId]!;
        var widgetVisual = (SpriteVisual)Get(widget, "Visual")!;
        var boxVisual = (SpriteVisual)Get(box, "Visual")!;
        var brush = (CompositionEffectBrush)Get(widget, "_blurBrush")!;
        Assert.Equal(1, widgetVisual.Opacity);
        Assert.Equal(.5f, boxVisual.Opacity);
        host.SetBlurStrength(0);
        Assert.Equal(0, boxVisual.Opacity);
        Assert.Equal(1, widgetVisual.Opacity);
        Assert.Equal(CompositionGetValueStatus.Succeeded, brush.Properties.TryGetScalar("BackdropBlur.BlurAmount", out var amount));
        Assert.Equal(15 * (float)scale, amount, precision: 3);
        host.PresentForeground(monitor.Id, frame, Point.Empty,
            [new(boxId, new(0, 0, 100, 160), 8, (float)scale), widgetRegion with { BlurStrength = 10 }]);
        Assert.Same(widget, regions[widgetId]);
        Assert.Equal(.2f, widgetVisual.Opacity);
        Assert.Equal(0, boxVisual.Opacity);
        Assert.Equal(CompositionGetValueStatus.Succeeded, brush.Properties.TryGetScalar("BackdropBlur.BlurAmount", out amount));
        Assert.Equal(0, amount);
    });

    [Fact]
    public void RuntimeWidgetSettersSaveAndKeepBoxMaterialIndependent()
    {
        var runtime = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        var changed = 0;
        runtime.Changed += (_, _) => changed++;
        var box = runtime.State.Boxes[0].Appearance;
        box.Background = "#FFABCDEF";
        box.BackgroundOpacity = .8;
        runtime.State.Settings.Appearance.AcrylicBlurStrength = 23;
        runtime.SetWidgetAcrylicFollowBox(false);
        runtime.SetWidgetAcrylicBackground("#80345678");
        runtime.SetWidgetAcrylicOpacity(.19);
        runtime.SetWidgetAcrylicBlurStrength(85);
        var settings = runtime.State.Settings.Appearance.WidgetAcrylic;
        Assert.Equal("#FF345678", settings.Background);
        Assert.Equal(.19, settings.BackgroundOpacity);
        Assert.Equal(85, settings.BlurStrength);
        Assert.False(settings.FollowBoxAppearance);
        runtime.SetWidgetAcrylicFollowBox(true);
        Assert.True(settings.FollowBoxAppearance);
        Assert.Equal("#FF345678", settings.Background);
        Assert.Equal(.19, settings.BackgroundOpacity);
        Assert.Equal(85, settings.BlurStrength);
        Assert.Equal(5, changed);
        Assert.NotNull(Get(timer, "_timer"));
        Assert.Equal("#FFABCDEF", box.Background);
        Assert.Equal(.8, box.BackgroundOpacity);
        Assert.Equal(23, runtime.State.Settings.Appearance.AcrylicBlurStrength);
        runtime.SetAcrylicBlurStrength(10);
        runtime.SetBoxOpacity(null, .6);
        Assert.Equal(.19, settings.BackgroundOpacity);
        Assert.Equal(85, settings.BlurStrength);
        runtime.SetWidgetAcrylicOpacity(-1);
        runtime.SetWidgetAcrylicBlurStrength(101);
        Assert.Equal(0, settings.BackgroundOpacity);
        Assert.Equal(100, settings.BlurStrength);
    }

    [Fact]
    public void WidgetAcrylicControlsAreGlobal()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CrabDesk.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var document = XDocument.Load(Path.Combine(directory!.FullName, "CrabDesk.WinUI", "Views", "WidgetsPage.xaml"));
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var group = document.Descendants(ui + "Border").Single(element => (string?)element.Attribute(x + "Name") == "WidgetAcrylicSettings");
        Assert.Contains(group.Ancestors(), element => (string?)element.Attribute(x + "Name") == "AppearanceView");
        Assert.DoesNotContain(group.Ancestors(), element => (string?)element.Attribute(x + "Name") == "EditorView");
        var entry = document.Descendants(ui + "Button").Single(element => (string?)element.Attribute(x + "Name") == "WidgetAppearanceButton");
        Assert.Equal("2", (string?)entry.Attribute("Grid.Column"));
        Assert.Contains(entry.Descendants(ui + "TextBlock"), element => (string?)element.Attribute("Text") == "外观设置");
        foreach (var name in new[] { "WidgetAcrylicOpacity", "WidgetAcrylicBlur" })
        {
            var slider = Assert.Single(group.Descendants(ui + "Slider").Where(element => (string?)element.Attribute(x + "Name") == name));
            Assert.Equal("0", (string?)slider.Attribute("Minimum"));
            Assert.Equal("100", (string?)slider.Attribute("Maximum"));
            Assert.Equal("1", (string?)slider.Attribute("StepFrequency"));
        }
        Assert.Single(group.Descendants(ui + "ColorPicker"));
        var follow = Assert.Single(group.Descendants(ui + "ToggleSwitch")
            .Where(element => (string?)element.Attribute(x + "Name") == "WidgetAcrylicFollowBox"));
        Assert.Equal("跟随盒子", (string?)follow.Attribute("Header"));
        var custom = group.Descendants(ui + "StackPanel")
            .Single(element => (string?)element.Attribute(x + "Name") == "WidgetAcrylicCustomSettings");
        Assert.Equal(2, custom.Descendants(ui + "Slider").Count());
    }

    [Theory]
    [InlineData(1d)]
    [InlineData(1.5d)]
    public void NativeBlurControlsUpdateExistingBrushesAndLeaveForegroundOpaque(double scale) => OnSta(() =>
    {
        using var desktop = new System.Windows.Forms.Form();
        var monitor = Monitor(scale);
        using var host = new DesktopAcrylicHost(desktop.Handle, [monitor], CrabDesk.WinUI.Services.AcrylicBlurEffects.CreateFactory);
        host.InitializeComposition();
        using var frame = DesktopLayerBitmapFactory.Create(200, 160);
        using (var graphics = Graphics.FromImage(frame)) graphics.Clear(Color.Transparent);
        var id = Guid.NewGuid();
        host.PresentForeground(monitor.Id, frame, Point.Empty, [new(id, new(0, 0, 200, 160), 8, (float)scale)]);
        var boxes = (IDictionary)Get(host, "_boxes")!;
        var box = boxes[id]!;
        var visual = (SpriteVisual)Get(box, "Visual")!;
        var brush = (CompositionEffectBrush)Get(box, "_blurBrush")!;
        foreach (var strength in new[] { 0, 25, 50, 75, 100, 50, 0 })
        {
            host.SetBlurStrength(strength);
            Assert.Same(box, boxes[id]);
            Assert.Equal(Math.Clamp(strength / 50f, 0, 1), visual.Opacity);
            Assert.Equal(CompositionGetValueStatus.Succeeded,
                brush.Properties.TryGetScalar("BackdropBlur.BlurAmount", out var amount));
            Assert.Equal(Math.Max(0, strength - 50) * .6f * (float)scale, amount);
            foreach (var foreground in ((IDictionary)Get(host, "_foregrounds")!).Values)
                Assert.Equal(1, ((SpriteVisual)Get(foreground!, "Visual")!).Opacity);
        }
    });

    [Fact]
    public void RuntimeSurfaceSettersKeepColorOpacityAndBlurIndependent()
    {
        var runtime = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        var appearance = runtime.State.Boxes[0].Appearance;
        appearance.BackgroundOpacity = .2;
        runtime.SetAcrylicBlurStrength(75);
        Assert.Equal(.2, appearance.ResolveBackgroundOpacity(true));
        runtime.SetBoxBackground(null, "#FF335A70");
        Assert.Equal(75, runtime.State.Settings.Appearance.AcrylicBlurStrength);
        Assert.Equal(.2, appearance.ResolveBackgroundOpacity(true));
        foreach (var value in new[] { -1d, 0d, .1d, .6d, 1d, 2d })
        {
            runtime.SetBoxOpacity(null, value);
            Assert.Equal(Math.Clamp(value, 0, 1), appearance.ResolveBackgroundOpacity(true));
            Assert.Equal(Math.Clamp(value, 0, 1), runtime.State.Settings.DefaultBoxAppearance.ResolveBackgroundOpacity(true));
            Assert.Equal(75, runtime.State.Settings.Appearance.AcrylicBlurStrength);
            Assert.Equal("#FF335A70", appearance.Background);
        }
        runtime.SetAcrylicBlurStrength(-1);
        Assert.Equal(0, runtime.State.Settings.Appearance.AcrylicBlurStrength);
        runtime.SetAcrylicBlurStrength(101);
        Assert.Equal(100, runtime.State.Settings.Appearance.AcrylicBlurStrength);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(.1d)]
    [InlineData(1d)]
    public void WidgetBackgroundUsesTheRequestedOpacity(double opacity) => OnSta(() =>
    {
        var runtime = Fixture();
        runtime.State.Settings.Appearance.ShowBorder = false;
        runtime.State.Boxes[0].Appearance.BackgroundOpacity = opacity;
        using var form = new DesktopBoxForm(runtime, runtime.Monitors[0]);
        form.SetAcrylicBackground(true);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Note, Bounds = new(0, 0, 180, 180) };
        using var bitmap = DesktopLayerBitmapFactory.Create(180, 180);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            typeof(DesktopBoxForm).GetMethod("DrawWidget", Hidden)!.Invoke(form, [graphics, widget]);
        }
        Assert.Equal((int)Math.Round(opacity * 255), bitmap.GetPixel(120, 140).A);
    });

    [Fact]
    public void AppearancePageOffersSeparateOpacityAndBlurSliders()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CrabDesk.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var document = XDocument.Load(Path.Combine(directory!.FullName, "CrabDesk.WinUI", "Views", "AppearancePage.xaml"));
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var name in new[] { "OpacitySlider", "AcrylicBlurStrengthSlider" })
        {
            var slider = document.Descendants(ui + "Slider").Single(element => (string?)element.Attribute(x + "Name") == name);
            Assert.Equal("0", (string?)slider.Attribute("Minimum"));
            Assert.Equal("100", (string?)slider.Attribute("Maximum"));
            Assert.Equal("1", (string?)slider.Attribute("StepFrequency"));
            Assert.Contains(name == "OpacitySlider" ? "Opacity" : "AcrylicBlurStrength", (string?)slider.Attribute("Value"));
        }
        var blur = document.Descendants(ui + "Slider").Single(element => (string?)element.Attribute(x + "Name") == "AcrylicBlurStrengthSlider");
        Assert.Equal("{Binding UseAcrylicBoxes}", (string?)blur.Attribute("IsEnabled"));
        Assert.Contains(document.Descendants(ui + "ColorPicker"), element => ((string?)element.Attribute("Color"))?.Contains("Binding Background") == true);
    }

    private static MonitorLayout Monitor(double scale) => new()
    {
        Id = "test", DeviceName = "test", DpiScale = scale, Bounds = new(0, 0, 800, 600), WorkArea = new(0, 0, 800, 600),
        PixelBounds = new(0, 0, 800 * scale, 600 * scale), PixelWorkArea = new(0, 0, 800 * scale, 600 * scale)
    };

    private static CrabDeskRuntime Fixture()
    {
        var runtime = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
        var state = new CrabDeskState();
        state.Settings.Appearance.UseAcrylicBoxes = true;
        state.Boxes.Add(new DesktopBox());
        Set(runtime, "<State>k__BackingField", state);
        Set(runtime, "<Items>k__BackingField", Array.Empty<DesktopItemRef>());
        Set(runtime, "<Monitors>k__BackingField", new[] { Monitor(1) });
        Set(runtime, "_beginInvoke", (Action<Action>)(_ => { }));
        return runtime;
    }

    private static object? Get(object target, string name) => target.GetType().GetField(name, Hidden)!.GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Hidden)!.SetValue(target, value);
    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception failure) { error = failure; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
