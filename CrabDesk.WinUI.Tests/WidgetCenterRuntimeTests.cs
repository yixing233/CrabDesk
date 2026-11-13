using System.Reflection;
using System.Runtime.CompilerServices;
using CrabDesk.Core;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class WidgetCenterRuntimeTests
{
    [Fact]
    public void CenterThemeChangesAreIndependentAndOnlyNotifyWhenTheSavedModeChanges()
    {
        using var fixture = new Fixture();
        var runtime = fixture.Runtime;
        runtime.State.Settings.ThemeMode = ApplicationThemeMode.Dark;
        var widget = runtime.AddWidget(DesktopWidgetKind.Note);
        widget.ColorTheme = WidgetColorTheme.Signature;
        widget.BackgroundMaterial = WidgetBackgroundMaterial.Acrylic;
        var notifications = 0;
        runtime.Changed += (_, _) => notifications++;
        runtime.SetWidgetCenterThemeMode(ApplicationThemeMode.Light);
        Assert.False(runtime.IsWidgetCenterDarkTheme);
        runtime.SetWidgetCenterThemeMode(ApplicationThemeMode.Dark);
        Assert.True(runtime.IsWidgetCenterDarkTheme);
        runtime.SetWidgetCenterThemeMode(ApplicationThemeMode.Dark);
        Assert.Equal(2, notifications);
        Assert.Equal(ApplicationThemeMode.Dark, runtime.State.Settings.ThemeMode);
        Assert.False(runtime.State.Settings.WidgetCenter.Enabled);
        Assert.Equal(WidgetPlacement.Desktop, widget.Placement);
        Assert.Equal(WidgetBackgroundMaterial.Acrylic, widget.BackgroundMaterial);
        Assert.Equal(WidgetColorTheme.Signature, widget.ColorTheme);
        Assert.Throws<ArgumentOutOfRangeException>(() => runtime.SetWidgetCenterThemeMode((ApplicationThemeMode)99));
        Assert.Equal(ApplicationThemeMode.Dark, runtime.State.Settings.WidgetCenter.ThemeMode);
        Assert.Equal(2, notifications);
    }

    [Theory]
    [InlineData(ApplicationThemeMode.Light)]
    [InlineData(ApplicationThemeMode.Dark)]
    public void SystemFollowingCenterReceivesPreferenceChangesWhileApplicationThemeIsFixed(ApplicationThemeMode appMode)
    {
        using var fixture = new Fixture();
        var runtime = fixture.Runtime;
        runtime.State.Settings.ThemeMode = appMode;
        var systemDark = ApplicationTheme.ResolveIsDark(ApplicationThemeMode.System);
        Set(runtime, "<IsWidgetCenterDarkTheme>k__BackingField", !systemDark);
        var notifications = 0;
        runtime.Changed += (_, _) => notifications++;
        typeof(CrabDeskRuntime).GetMethod("OnSystemPreferenceChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(runtime, [null, new Microsoft.Win32.UserPreferenceChangedEventArgs(Microsoft.Win32.UserPreferenceCategory.Color)]);
        Assert.Equal(systemDark, runtime.IsWidgetCenterDarkTheme);
        Assert.Equal(appMode == ApplicationThemeMode.Dark, runtime.IsDarkTheme);
        Assert.Equal(ApplicationThemeMode.System, runtime.State.Settings.WidgetCenter.ThemeMode);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void ApplicationThemeChangesKeepAnExplicitCenterTheme()
    {
        using var fixture = new Fixture();
        fixture.Runtime.SetWidgetCenterThemeMode(ApplicationThemeMode.Dark);
        fixture.Runtime.SetThemeMode(ApplicationThemeMode.Light);
        Assert.False(fixture.Runtime.IsDarkTheme);
        Assert.True(fixture.Runtime.IsWidgetCenterDarkTheme);
        Assert.Equal(ApplicationThemeMode.Dark, fixture.Runtime.State.Settings.WidgetCenter.ThemeMode);
    }

    [Fact]
    public void EnablingDoesNotMoveDesktopWidgetsAndDisablingKeepsCenterContents()
    {
        using var fixture = new Fixture();
        var widget = fixture.Runtime.AddWidget(DesktopWidgetKind.Note);
        var original = widget.Bounds;
        fixture.Runtime.SetWidgetCenterEnabled(true);
        Assert.Equal(WidgetPlacement.Desktop, widget.Placement);
        Assert.False(fixture.Runtime.IsWidgetCenterVisible);
        fixture.Runtime.MoveWidgetToCenter(widget);
        widget.Text = "keep this note";
        fixture.Runtime.SetWidgetCenterEnabled(false);
        Assert.Equal(WidgetPlacement.Center, widget.Placement);
        Assert.Equal(original, widget.Bounds);
        Assert.Equal("keep this note", widget.Text);
        Assert.False(fixture.Runtime.IsWidgetCenterVisible);
        fixture.Runtime.MoveWidgetToDesktop(widget);
        Assert.Equal(WidgetPlacement.Desktop, widget.Placement);
        Assert.Equal(original, widget.Bounds);
    }

    [Fact]
    public void CenterAddSetsPlacementBeforeFirstNotificationAndDoesNotDuplicateKinds()
    {
        using var fixture = new Fixture();
        fixture.Runtime.SetWidgetCenterEnabled(true);
        var observed = new List<WidgetPlacement>();
        fixture.Runtime.Changed += (_, _) => observed.Add(fixture.Runtime.State.Widgets.Single().Placement);
        var widget = fixture.Runtime.AddWidgetToCenter(DesktopWidgetKind.Note);
        Assert.Equal(new[] { WidgetPlacement.Center }, observed);
        Assert.Same(widget, fixture.Runtime.AddWidgetToCenter(DesktopWidgetKind.Note));
        Assert.Single(fixture.Runtime.State.Widgets);
        Assert.Throws<InvalidOperationException>(() =>
        {
            fixture.Runtime.SetWidgetCenterEnabled(false);
            fixture.Runtime.AddWidgetToCenter(DesktopWidgetKind.Calendar);
        });
    }

    [Fact]
    public void CenterHotkeyIsOnlyRegisteredWhileFeatureIsEnabled()
    {
        using var fixture = new Fixture();
        fixture.Runtime.SetHotkey(HotkeyAction.WidgetCenter, true, HotkeyModifiers.Control | HotkeyModifiers.Alt, HotkeyKey.W);
        Assert.False(fixture.Hotkeys.Bindings[HotkeyAction.WidgetCenter].Enabled);
        fixture.Runtime.SetWidgetCenterEnabled(true);
        Assert.True(fixture.Hotkeys.Bindings[HotkeyAction.WidgetCenter].Enabled);
        fixture.Runtime.SetWidgetCenterEnabled(false);
        Assert.False(fixture.Hotkeys.Bindings[HotkeyAction.WidgetCenter].Enabled);
        Assert.True(fixture.Runtime.State.Settings.Hotkeys.WidgetCenter.Enabled);
    }

    [Fact]
    public void PausedDesktopDoesNotDisableVisibleCenterWidgets()
    {
        using var fixture = new Fixture();
        fixture.Runtime.SetWidgetCenterEnabled(true);
        var widget = fixture.Runtime.AddWidgetToCenter(DesktopWidgetKind.Note);
        Set(fixture.Runtime, "<IsPaused>k__BackingField", true);
        Assert.False(fixture.Runtime.IsWidgetActive(widget));
        Set(fixture.Runtime, "_widgetCenterVisible", true);
        Assert.True(fixture.Runtime.IsWidgetActive(widget));
        widget.Placement = WidgetPlacement.Desktop;
        Assert.False(fixture.Runtime.IsWidgetActive(widget));
        fixture.Runtime.HideWidgetCenter();
        Assert.False(fixture.Runtime.IsWidgetCenterVisible);
    }

    [Fact]
    public void TrayClickAfterDeactivateDoesNotReopenTheJustHiddenCenter()
    {
        using var fixture = new Fixture();
        fixture.Runtime.SetWidgetCenterEnabled(true);
        Set(fixture.Runtime, "_widgetCenterVisible", true);
        fixture.Runtime.HideWidgetCenterOnDeactivate();
        Assert.False(fixture.Runtime.IsWidgetCenterVisible);
        fixture.Runtime.OnTrayLeftClick();
        Assert.False(fixture.Runtime.IsWidgetCenterVisible);
        Assert.True(fixture.Runtime.State.Settings.WidgetCenter.Enabled);
    }

    [Fact]
    public void DisabledTrayClickAndCenterHotkeyKeepExistingSettingsBehavior()
    {
        using var fixture = new Fixture();
        var settings = 0;
        fixture.Runtime.ShowSettingsRequested += (_, _) => settings++;
        fixture.Runtime.OnTrayLeftClick();
        Assert.Equal(1, settings);
        var handler = typeof(CrabDeskRuntime).GetMethod("OnGlobalHotkeyPressed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        handler.Invoke(fixture.Runtime, new object?[] { null, new GlobalHotkeyPressedEventArgs(HotkeyAction.WidgetCenter) });
        Assert.Equal(1, settings);
        Assert.False(fixture.Runtime.IsWidgetCenterVisible);
        Assert.Empty(fixture.Runtime.State.Boxes);
    }

    internal sealed class Fixture : IDisposable
    {
        internal CrabDeskRuntime Runtime { get; } = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
        internal FakeHotkeys Hotkeys { get; } = new();
        private readonly RuntimeTimer _save = new(TimeSpan.FromHours(1), false, _ => { }, () => { });
        internal Fixture()
        {
            var monitor = new MonitorLayout { Id = "center-test", DeviceName = "center-test", IsPrimary = true, DpiScale = 1,
                Bounds = new(0, 0, 1200, 900), WorkArea = new(0, 0, 1200, 850),
                PixelBounds = new(0, 0, 1200, 900), PixelWorkArea = new(0, 0, 1200, 850) };
            Set(Runtime, "<State>k__BackingField", new CrabDeskState());
            Set(Runtime, "<Monitors>k__BackingField", new[] { monitor });
            Set(Runtime, "_saveTimer", _save);
            Set(Runtime, "_beginInvoke", (Action<Action>)(action => action()));
            Set(Runtime, "_hotkeyService", Hotkeys);
            Set(Runtime, "_hotkeyStatuses", new Dictionary<HotkeyAction, HotkeyRegistrationStatus>());
        }
        public void Dispose() => _save.Dispose();
    }

    internal sealed class FakeHotkeys : IHotkeyService
    {
        internal Dictionary<HotkeyAction, HotkeyBinding> Bindings { get; } = [];
        public event EventHandler<GlobalHotkeyPressedEventArgs>? Pressed { add { } remove { } }
        public HotkeyRegistrationStatus Register(HotkeyAction action, HotkeyBinding binding)
        {
            Bindings[action] = new() { Enabled = binding.Enabled, Modifiers = binding.Modifiers, Key = binding.Key };
            return binding.Enabled ? HotkeyRegistrationStatus.Registered : HotkeyRegistrationStatus.Disabled;
        }
        public void Unregister(HotkeyAction action) => Bindings.Remove(action);
        public void Dispose() { }
    }

    private static void Set(object instance, string name, object value) =>
        typeof(CrabDeskRuntime).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
}
