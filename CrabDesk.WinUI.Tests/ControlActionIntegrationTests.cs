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
        var body = WidgetMonitorPresentation.Body(widget.Bounds.Width, widget.Bounds.Height, 8);
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
