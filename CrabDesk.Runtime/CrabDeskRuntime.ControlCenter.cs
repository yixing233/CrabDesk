using System.Diagnostics;
using CrabDesk.Core;
using CrabDesk.Native;

namespace CrabDesk.Runtime;

public sealed partial class CrabDeskRuntime
{
    private ControlCenterSnapshot? _controlFast;
    private bool _controlRefreshPending;
    private long _controlLastRefreshTick;

    private DesktopWidget? ControlCenterWidget => State.Widgets.FirstOrDefault(w => w.Kind == DesktopWidgetKind.ControlCenter);

    /// <summary>
    /// Applies a locally known value straight to the card.
    ///
    /// Every command used to wait for a full sample to travel back through the
    /// polling loop, which took a second or more and made sliders feel like they
    /// were lagging behind the pointer. Painting the value that was just applied
    /// removes that wait; the next sample confirms it.
    /// </summary>
    private void ApplyControlValue(Action<ControlCenterSnapshot> update)
    {
        if (ControlCenterWidget is not { } widget) return;
        var current = _controlFast ?? widget.Controls ?? ControlCenterSnapshot.Empty;
        var updated = current with { };
        update(updated);
        _controlFast = updated;
        widget.Controls = MergeControlValues(widget.Controls, updated);
        _surfaceManager?.RefreshMonitorVisuals();
    }

    /// <summary>
    /// Overlays the locally applied values onto the last full snapshot so a
    /// partial update never blanks the readings it does not carry.
    /// </summary>
    private static ControlCenterSnapshot MergeControlValues(ControlCenterSnapshot? full, ControlCenterSnapshot local) =>
        full is null ? local : full with
        {
            VolumePercent = local.VolumePercent ?? full.VolumePercent,
            VolumeMuted = local.VolumeMuted ?? full.VolumeMuted,
            BrightnessPercent = local.BrightnessPercent ?? full.BrightnessPercent,
            OutputDeviceId = local.OutputDeviceId.Length > 0 ? local.OutputDeviceId : full.OutputDeviceId,
            OutputDeviceName = local.OutputDeviceName.Length > 0 ? local.OutputDeviceName : full.OutputDeviceName
        };

    /// <summary>
    /// Sets volume and paints it at once.
    ///
    /// While a slider is dragged this runs on every mouse move, so the cheap part
    /// - updating the number the card draws - happens unconditionally, and the
    /// read-back that costs a COM round trip is throttled. Doing both per move
    /// made the drag stutter.
    /// </summary>
    public void SetControlVolume(double percent)
    {
        if (_disposed) return;
        _controlMonitor ??= new WidgetControlCenterMonitor();
        var level = ControlCenterPresentation.ClampPercent(percent);
        var widget = ControlCenterWidget;
        if (widget?.Controls is { } current)
        {
            widget.Controls = current with { VolumePercent = level, VolumeMuted = false };
            _surfaceManager?.RefreshMonitorVisuals();
        }
        if (!_controlMonitor.SetVolume(level)) return;
        ScheduleControlRefresh();
    }

    public void SetControlBrightness(double percent)
    {
        if (_disposed) return;
        _controlMonitor ??= new WidgetControlCenterMonitor();
        var level = ControlCenterPresentation.ClampPercent(percent);
        var widget = ControlCenterWidget;
        if (widget?.Controls is { } current)
        {
            widget.Controls = current with { BrightnessPercent = level };
            _surfaceManager?.RefreshMonitorVisuals();
        }
        if (!_controlMonitor.SetBrightness(level)) return;
        ScheduleControlRefresh();
    }

    /// <summary>
    /// Coalesces read-backs during a drag: at most one every 150 ms, with a final
    /// one after the last change so the settled value is confirmed.
    /// </summary>
    private void ScheduleControlRefresh()
    {
        var now = Stopwatch.GetTimestamp();
        if (_controlRefreshPending) return;
        if (_controlLastRefreshTick != 0 &&
            Stopwatch.GetElapsedTime(_controlLastRefreshTick, now).TotalMilliseconds < 150) return;
        _controlRefreshPending = true;
        _controlLastRefreshTick = now;
        _ = Task.Run(async () =>
        {
            await Task.Delay(150).ConfigureAwait(false);
            _controlRefreshPending = false;
            RefreshControlFast();
        });
    }

    public void SetControlMuted(bool muted)
    {
        if (_disposed) return;
        _controlMonitor ??= new WidgetControlCenterMonitor();
        if (_controlMonitor.SetMuted(muted))
        {
            var widget = ControlCenterWidget;
            if (widget?.Controls is { } current) widget.Controls = current with { VolumeMuted = muted };
            _surfaceManager?.RefreshMonitorVisuals();
        }
        RefreshControlFast();
    }

    /// <summary>
    /// Reads the cheap controls and pushes them to the card. Called after every
    /// command so the displayed state matches the machine without waiting for the
    /// next poll; the poll itself continues in the background.
    /// </summary>
    private void RefreshControlFast()
    {
        var widget = ControlCenterWidget;
        if (widget is null || _controlMonitor is null) return;
        var refresh = _controlMonitor.ReadFast();
        _controlFast = refresh;
        widget.Controls = MergeControlValues(widget.Controls, refresh);
        _surfaceManager?.RefreshMonitorVisuals();
    }

    public async Task ToggleControlAsync(DesktopWidget widget, ControlCenterToggle toggle, bool enabled)
    {
        if (_disposed || widget.Kind != DesktopWidgetKind.ControlCenter || !State.Widgets.Contains(widget)) return;
        _controlMonitor ??= new WidgetControlCenterMonitor();

        // Show the intended state immediately: a radio transition takes a moment
        // in the driver, and until now the tile looked dead for over a second.
        if (widget.Controls is { } before)
        {
            widget.Controls = toggle switch
            {
                ControlCenterToggle.WiFi => before with { WiFiEnabled = enabled },
                ControlCenterToggle.Bluetooth => before with { BluetoothEnabled = enabled },
                _ => before with { NightLightEnabled = enabled }
            };
            _surfaceManager?.RefreshMonitorVisuals();
        }

        var ok = toggle == ControlCenterToggle.NightLight
            ? _controlMonitor.SetNightLight(enabled)
            : await _controlMonitor.SetRadioAsync(toggle, enabled).ConfigureAwait(false);

        if (!ok)
        {
            // Roll back rather than leave a tile claiming a state the machine is
            // not in.
            if (widget.Controls is { } current)
            {
                widget.Controls = toggle switch
                {
                    ControlCenterToggle.WiFi => current with { WiFiEnabled = !enabled },
                    ControlCenterToggle.Bluetooth => current with { BluetoothEnabled = !enabled },
                    _ => current with { NightLightEnabled = !enabled }
                };
            }
            widget.MonitorError = "无法切换" + ControlCenterPresentation.ToggleLabel(toggle);
            _surfaceManager?.RefreshMonitorVisuals();
            return;
        }
        widget.MonitorError = "";
        // Confirm against the machine rather than trusting the optimistic write.
        await RefreshMonitorWidgetsAsync(force: true).ConfigureAwait(false);
    }

    public async Task SetControlOutputAsync(string deviceId)
    {
        if (_disposed || string.IsNullOrEmpty(deviceId)) return;
        _controlMonitor ??= new WidgetControlCenterMonitor();
        if (_controlMonitor.SetOutputDevice(deviceId))
        {
            // Reflect the new name at once; the endpoint switch itself settles
            // asynchronously in the audio service.
            if (ControlCenterWidget is { } widget && widget.Controls is { } current)
            {
                var name = current.OutputDevices.FirstOrDefault(d => d.Id == deviceId)?.Name ?? current.OutputDeviceName;
                widget.Controls = current with { OutputDeviceId = deviceId, OutputDeviceName = name };
                _surfaceManager?.RefreshMonitorVisuals();
            }
            await Task.Delay(80).ConfigureAwait(false);
            RefreshControlFast();
            await RefreshMonitorWidgetsAsync(force: true).ConfigureAwait(false);
        }
    }

    public async Task SendControlMediaCommandAsync(ControlCenterMediaCommand command)
    {
        if (_disposed) return;
        if (!await WidgetControlCenterMonitor.MediaCommandAsync(command).ConfigureAwait(false)) return;
        // Flip the play state immediately so the button responds on click.
        if (command == ControlCenterMediaCommand.PlayPause && ControlCenterWidget is { } widget &&
            widget.Controls?.Media is { } media)
        {
            widget.Controls = widget.Controls with { Media = media with { IsPlaying = !media.IsPlaying, CapturedAt = DateTimeOffset.Now } };
            _surfaceManager?.RefreshMonitorVisuals();
        }
        await Task.Delay(150).ConfigureAwait(false);
        await RefreshMonitorWidgetsAsync(force: true).ConfigureAwait(false);
    }
}
