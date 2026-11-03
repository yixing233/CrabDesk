using CrabDesk.Core;
using CrabDesk.Native;

namespace CrabDesk.Runtime;

public sealed partial class CrabDeskRuntime
{
    /// <summary>
    /// Toggles a switch and refreshes the card.
    ///
    /// The refresh is forced rather than waiting for the next poll: a switch that
    /// takes two seconds to show its new state reads as a broken button, and the
    /// user's next click would land on stale state.
    /// </summary>
    public async Task ToggleControlAsync(DesktopWidget widget, ControlCenterToggle toggle, bool enabled)
    {
        if (_disposed || widget.Kind != DesktopWidgetKind.ControlCenter || !State.Widgets.Contains(widget)) return;
        _controlMonitor ??= new WidgetControlCenterMonitor();
        var ok = toggle == ControlCenterToggle.NightLight
            ? _controlMonitor.SetNightLight(enabled)
            : await _controlMonitor.SetRadioAsync(toggle, enabled).ConfigureAwait(false);
        if (!ok) widget.MonitorError = "无法切换" + ControlCenterPresentation.ToggleLabel(toggle);
        else widget.MonitorError = "";
        await RefreshMonitorWidgetsAsync(force: true).ConfigureAwait(false);
        NotifyControlCenterChanged();
    }

    public void SetControlVolume(double percent)
    {
        if (_disposed) return;
        _controlMonitor ??= new WidgetControlCenterMonitor();
        if (_controlMonitor.SetVolume(percent)) _ = RefreshMonitorWidgetsAsync(force: true);
    }

    public void SetControlBrightness(double percent)
    {
        if (_disposed) return;
        _controlMonitor ??= new WidgetControlCenterMonitor();
        if (_controlMonitor.SetBrightness(percent)) _ = RefreshMonitorWidgetsAsync(force: true);
    }

    public async Task SetControlOutputAsync(string deviceId)
    {
        if (_disposed || string.IsNullOrEmpty(deviceId)) return;
        _controlMonitor ??= new WidgetControlCenterMonitor();
        if (_controlMonitor.SetOutputDevice(deviceId))
        {
            await Task.Delay(120).ConfigureAwait(false);
            await RefreshMonitorWidgetsAsync(force: true).ConfigureAwait(false);
            NotifyControlCenterChanged();
        }
    }

    public async Task SendControlMediaCommandAsync(ControlCenterMediaCommand command)
    {
        if (_disposed) return;
        if (await WidgetControlCenterMonitor.MediaCommandAsync(command).ConfigureAwait(false))
        {
            // Give the player a moment to report the new state before reading it.
            await Task.Delay(250).ConfigureAwait(false);
            await RefreshMonitorWidgetsAsync(force: true).ConfigureAwait(false);
            NotifyControlCenterChanged();
        }
    }

    private void NotifyControlCenterChanged() => _surfaceManager?.RefreshMonitorVisuals();
}
