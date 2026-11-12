using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using CrabDesk.Core;
using CrabDesk.Native;
using CrabDesk.Runtime;
using Moq;
using Xunit;
using Forms = System.Windows.Forms;

namespace CrabDesk.WinUI.Tests;

[Collection("desktop-probe")]
public sealed class ControlActionIntegrationTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void DetailPopupInheritsWidgetMaterialInsteadOfApplicationTheme(bool dark, bool boxOverride, bool acrylic) => OnSta(() =>
    {
        var (runtime, widget) = Fixture();
        Set(runtime, "<IsDarkTheme>k__BackingField", !dark);
        var color = dark ? Color.FromArgb(28, 37, 53) : Color.FromArgb(241, 245, 249);
        var background = $"#FF{color.R:X2}{color.G:X2}{color.B:X2}";
        runtime.State.Settings.DefaultBoxAppearance.Background = boxOverride ? "#FF804020" : background;
        runtime.State.Settings.DefaultBoxAppearance.BackgroundOpacity = .43;
        var settings = runtime.State.Settings.Appearance;
        settings.UseAcrylicBoxes = acrylic;
        settings.BorderWidth = 2;
        settings.BorderOpacity = 35;
        if (boxOverride)
            runtime.State.Boxes.Add(new DesktopBox { Appearance = new BoxAppearance { Background = background, BackgroundOpacity = .43 } });
        using var form = new DesktopBoxForm(runtime, runtime.Monitors[0]);

        Call(form, "ShowQuickActionsEditor", widget);

        using var popup = Assert.IsType<QuickActionsEditorPopup>(typeof(DesktopBoxForm)
            .GetField("_controlPopup", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form));
        Assert.Equal(color.ToArgb(), popup.BackColor.ToArgb());
        var content = Assert.IsType<System.Windows.Controls.Border>(popup.ContentView);
        var surface = Assert.IsType<System.Windows.Media.SolidColorBrush>(content.Background);
        Assert.Equal(System.Windows.Media.Color.FromArgb((byte)Math.Round(255 * .43), color.R, color.G, color.B), surface.Color);
        Assert.Equal(new System.Windows.CornerRadius(8), content.CornerRadius);
        Assert.Equal(new System.Windows.Thickness(0), content.BorderThickness);
        Assert.Null(content.BorderBrush);
        Assert.IsType<System.Windows.Interop.HwndSource>(System.Windows.PresentationSource.FromVisual(content));
        var ink = Assert.IsType<System.Windows.Media.SolidColorBrush>(typeof(ControlCenterPopup)
            .GetField("ForegroundInk", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(popup));
        Assert.Equal(dark ? System.Windows.Media.Color.FromRgb(242, 244, 248) :
            System.Windows.Media.Color.FromRgb(31, 35, 41), ink.Color);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NightActionAppliesTheRequestedThemeAndPublishesItsReadBack(bool initialDark)
    {
        var (runtime, widget) = Fixture();
        var state = new ControlCenterSnapshot { NightLightEnabled = initialDark };
        widget.Controls = state;
        Set(runtime, "_beginInvoke", (Action<Action>)(action => action()));
        var source = new Mock<IWidgetControlCenterMonitor>();
        source.Setup(monitor => monitor.SetNightLight(!initialDark)).Returns(() =>
        {
            state = state with { NightLightEnabled = !initialDark };
            return true;
        });
        source.Setup(monitor => monitor.ReadAsync(It.IsAny<CancellationToken>())).Returns(() => Task.FromResult(state));
        Set(runtime, "_controlMonitor", source.Object);

        await runtime.ToggleControlAsync(widget, ControlCenterToggle.NightLight, !initialDark);

        source.Verify(monitor => monitor.SetNightLight(!initialDark), Times.Once);
        Assert.Equal(!initialDark, widget.Controls!.NightLightEnabled);
        Assert.Empty(widget.MonitorError);
    }

    [Theory]
    [InlineData(ControlCenterQuickAction.WiFi, false)]
    [InlineData(ControlCenterQuickAction.WiFi, true)]
    [InlineData(ControlCenterQuickAction.Bluetooth, false)]
    [InlineData(ControlCenterQuickAction.Bluetooth, true)]
    public void ConnectionDetailsButtonsUseHandCursorAcrossTheirWholeTarget(ControlCenterQuickAction action, bool locked) => OnSta(() =>
    {
        var (runtime, widget) = Fixture();
        widget.IsLocked = locked;
        widget.ControlCenterQuickActions = [ControlCenterQuickAction.WiFi, ControlCenterQuickAction.Bluetooth];
        using var form = new DesktopBoxForm(runtime, runtime.Monitors[0]);
        var body = ControlCenterPresentation.Body(widget.Bounds.Width, widget.Bounds.Height, 8);
        var tile = ControlCenterPresentation.QuickAction(body, action == ControlCenterQuickAction.WiFi ? 0 : 1, 2);
        var details = ControlCenterPresentation.QuickActionDetails(tile);
        foreach (var offset in new[] { 2, 14, 30 })
        {
            var point = new PointF((float)(widget.Bounds.X + details.X + details.Width / 2),
                (float)(widget.Bounds.Y + details.Y + offset));
            Assert.Equal(action == ControlCenterQuickAction.WiFi
                    ? ControlCenterPresentation.ControlCenterTarget.WiFiDetails
                    : ControlCenterPresentation.ControlCenterTarget.BluetoothDetails,
                Call(form, "ControlCenterTargetAt", widget, point));
            Assert.True((bool)Call(form, "HandleWidgetMouseMove", point)!);
            Assert.Equal(Forms.Cursors.Hand, form.Cursor);
        }
        var header = new PointF((float)(widget.Bounds.X + widget.Bounds.Width / 2), (float)(widget.Bounds.Y + 10));
        Call(form, "HandleWidgetMouseMove", header);
        Assert.Equal(locked ? Forms.Cursors.Default : Forms.Cursors.SizeAll, form.Cursor);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AudioPopupSecondClickIsConsumedForBothQuickActionAndOriginalButton(bool microphone, bool quickAction) => OnSta(() =>
    {
        var (runtime, widget) = Fixture();
        using var form = new DesktopBoxForm(runtime, runtime.Monitors[0]);
        var audio = new Mock<IAudioMixer>();
        audio.Setup(source => source.Read()).Returns(AudioMixerSnapshot.Empty);
        var capture = new Mock<IMicrophone>();
        capture.Setup(source => source.Read()).Returns(MicrophoneSnapshot.Empty);
        using Forms.Form popup = microphone
            ? new MicrophonePopup(new MicrophoneController(capture.Object), false, Color.RoyalBlue, 1)
            : new AudioMixerPopup(new AudioMixerController(audio.Object), false, Color.RoyalBlue, 1);
        Set(form, microphone ? "_microphonePopup" : "_controlMixerPopup", popup);
        Set(form, microphone ? "_microphoneWidget" : "_controlMixerWidget", widget);
        Set(form, microphone ? "_microphoneBounds" : "_controlMixerBounds", widget.Bounds);
        popup.Show(form);
        Assert.True(popup.Visible);

        var point = EntryPoint(widget, microphone, quickAction);
        var args = new Forms.MouseEventArgs(Forms.MouseButtons.Left, 1, (int)Math.Round(point.X), (int)Math.Round(point.Y), 0);
        // Match the desktop mouse sequence: remember the source, close inputs,
        // then consume the release so the just-closed popup is not recreated.
        Call(form, "CaptureControlPopupToggle", args);
        form.DismissWidgetInputs();

        Assert.False(popup.Visible);
        Assert.True((bool)Call(form, "ConsumeClosedControlPopupClick", widget, point)!);
        Assert.False((bool)Call(form, "ConsumeClosedControlPopupClick", widget, point)!);
        audio.Verify(source => source.SetVolume(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<double>()), Times.Never);
        capture.Verify(source => source.SetVolume(It.IsAny<string>(), It.IsAny<double>()), Times.Never);
    });

    private static PointF EntryPoint(DesktopWidget widget, bool microphone, bool quickAction)
    {
        var body = ControlCenterPresentation.Body(widget.Bounds.Width, widget.Bounds.Height, 8);
        LayoutRect entry;
        if (quickAction)
            entry = ControlCenterPresentation.QuickAction(body, microphone ? 2 : 1, 3);
        else
            entry = microphone ? ControlCenterPresentation.MicrophoneButton(body) : ControlCenterPresentation.VolumeMixerButton(body);
        return new((float)(widget.Bounds.X + entry.X + entry.Width / 2),
            (float)(widget.Bounds.Y + entry.Y + entry.Height / 2));
    }

    private static (CrabDeskRuntime Runtime, DesktopWidget Widget) Fixture()
    {
        var runtime = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
        Set(runtime, "<State>k__BackingField", new CrabDeskState());
        runtime.State.Settings.Appearance.CornerRadius = 8;
        runtime.State.Settings.Appearance.AnimationEnabled = false;
        Set(runtime, "<Items>k__BackingField", Array.Empty<DesktopItemRef>());
        Set(runtime, "<Monitors>k__BackingField", new[] { new MonitorLayout
        {
            Id = "test", DeviceName = "test", IsPrimary = true,
            Bounds = new(0, 0, 1000, 700), WorkArea = new(0, 0, 1000, 700),
            PixelBounds = new(0, 0, 1000, 700), PixelWorkArea = new(0, 0, 1000, 700)
        } });
        Set(runtime, "_beginInvoke", (Action<Action>)(_ => { }));
        Set(runtime, "_controlMonitor", new Mock<IWidgetControlCenterMonitor>().Object);
        var widget = new DesktopWidget
        {
            Kind = DesktopWidgetKind.ControlCenter, MonitorId = "test", Size = DesktopWidgetSize.Panel,
            Bounds = new(100, 100, 372, 292),
            ControlCenterQuickActions = [ControlCenterQuickAction.Display, ControlCenterQuickAction.ApplicationVolume, ControlCenterQuickAction.Microphone]
        };
        runtime.State.Widgets.Add(widget);
        return (runtime, widget);
    }

    private static void Set(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static object? Call(object target, string name, params object[] arguments) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { failure = exception; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
