using CrabDesk.Core;
using CrabDesk.Native;

namespace CrabDesk.Runtime;

public sealed partial class CrabDeskRuntime
{
    public void SetControlQuickActions(DesktopWidget widget, IEnumerable<ControlCenterQuickAction> actions)
    {
        if (_disposed || widget.Kind != DesktopWidgetKind.ControlCenter || !State.Widgets.Contains(widget)) return;
        var selected = ControlQuickActions.Normalize(actions);
        if (ControlQuickActions.For(widget).SequenceEqual(selected)) return;
        widget.ControlCenterQuickActions = selected;
        RefreshWidgetPosition(widget, save: true);
    }

    private object? _controlQueueGate;
    private SemaphoreSlim? _controlIoGate;
    private object ControlQueueGate => LazyInitializer.EnsureInitialized(ref _controlQueueGate)!;
    private SemaphoreSlim ControlIoGate => LazyInitializer.EnsureInitialized(ref _controlIoGate, () => new SemaphoreSlim(1, 1));
    private double? _pendingControlVolume, _pendingControlBrightness;
    private bool? _pendingControlMuted;
    private DesktopWidget? _pendingControlWidget;
    private string _pendingControlDisplayId = "";
    private int _controlWriting;
    private long _controlCommandVersion;
    private Task? _controlWriteTask;
    private DesktopWidget? ControlCenterWidget => State.Widgets.FirstOrDefault(w => w.Kind == DesktopWidgetKind.ControlCenter);

    private async Task<ControlCenterSnapshot> ReadControlSnapshotAsync(IWidgetControlCenterMonitor source, CancellationToken token, string displayId)
    {
        await ControlIoGate.WaitAsync(token).ConfigureAwait(false);
        try { source.BrightnessDisplayId = displayId; return await source.ReadAsync(token).ConfigureAwait(false); }
        finally { ControlIoGate.Release(); }
    }

    private void PublishControlResult(DesktopWidget widget, long version, ControlCenterSnapshot? snapshot, string error)
    {
        if (_disposed) return;
        _beginInvoke(() =>
        {
            if (_disposed || IsPaused || !State.Widgets.Contains(widget) || version != Volatile.Read(ref _controlCommandVersion)) return;
            if (snapshot is not null) widget.Controls = snapshot;
            widget.MonitorError = error;
            NotifyMonitorFrame();
        });
    }

    public void SetControlVolume(double percent) =>
        QueueControlChange(volume: ControlCenterPresentation.ClampPercent(percent), muted: false);
    public void SetControlBrightness(double percent) =>
        QueueControlChange(brightness: ControlCenterPresentation.ClampPercent(percent));
    public void SetControlMuted(bool muted) => QueueControlChange(muted: muted);

    private void QueueControlChange(double? volume = null, double? brightness = null, bool? muted = null)
    {
        if (_disposed || IsPaused || ControlCenterWidget is not { } widget) return;
        _controlMonitor ??= new WidgetControlCenterMonitor();
        if (widget.Controls is { } current)
        {
            widget.Controls = current with
            {
                VolumePercent = volume ?? current.VolumePercent,
                BrightnessPercent = brightness ?? current.BrightnessPercent,
                VolumeMuted = muted ?? current.VolumeMuted
            };
            NotifyMonitorFrame();
        }
        lock (ControlQueueGate)
        {
            Interlocked.Increment(ref _controlCommandVersion);
            _pendingControlWidget = widget;
            _pendingControlDisplayId = ResolveControlBrightnessDisplay(widget);
            if (volume is not null) _pendingControlVolume = volume;
            if (brightness is not null) _pendingControlBrightness = brightness;
            if (muted is not null) _pendingControlMuted = muted;
            if (_controlWriting != 0) return;
            Volatile.Write(ref _controlWriting, 1);
            _controlWriteTask = Task.Run(DrainControlLevelsAsync);
        }
    }

    // Coalesce pointer updates; serialize slow hardware writes and read-backs off
    // the UI thread. Keep draining until the last requested value has been sent.
    private async Task DrainControlLevelsAsync()
    {
        while (true)
        {
            await Task.Delay(150).ConfigureAwait(false);
            double? volume, brightness; bool? muted; DesktopWidget? widget; long version; string displayId;
            lock (ControlQueueGate)
            {
                volume = _pendingControlVolume; brightness = _pendingControlBrightness; widget = _pendingControlWidget;
                muted = _pendingControlMuted;
                _pendingControlVolume = _pendingControlBrightness = null;
                _pendingControlMuted = null;
                version = Volatile.Read(ref _controlCommandVersion);
                displayId = _pendingControlDisplayId;
                if (volume is null && brightness is null && muted is null) { Volatile.Write(ref _controlWriting, 0); return; }
            }
            if (widget is null || _disposed || IsPaused) continue;
            var source = _controlMonitor!;
            ControlCenterSnapshot? read = null; var error = "";
            await ControlIoGate.WaitAsync().ConfigureAwait(false);
            try
            {
                source.BrightnessDisplayId = displayId;
                var volumeWritten = volume is not { } v || source.SetVolume(v);
                if (!volumeWritten) error = "无法调整音量";
                if (muted is { } m && (m || volumeWritten) && !source.SetMuted(m))
                    error = m ? "无法静音" : "无法取消静音";
                if (brightness is { } b && !source.SetBrightness(b)) error = "无法调整亮度";
                read = source.ReadFast();
            }
            catch (Exception exception) { error = "控制设置失败：" + exception.Message; }
            finally { ControlIoGate.Release(); }
            var result = read; var message = error;
            if (_disposed) continue;
            _beginInvoke(() =>
            {
                if (_disposed || IsPaused || !State.Widgets.Contains(widget) || version != Volatile.Read(ref _controlCommandVersion)) return;
                if (result is not null && widget.Controls is { } current)
                    widget.Controls = current with { VolumePercent = result.VolumePercent, VolumeMuted = result.VolumeMuted,
                        BrightnessPercent = result.BrightnessPercent, OutputDeviceId = result.OutputDeviceId, OutputDeviceName = result.OutputDeviceName };
                widget.MonitorError = message;
                NotifyMonitorFrame();
            });
        }
    }

    private async Task RunControlCommandAsync(DesktopWidget widget, Func<IWidgetControlCenterMonitor, Task<bool>> command, string failure)
    {
        if (_disposed || IsPaused || !State.Widgets.Contains(widget)) return;
        var source = _controlMonitor ??= new WidgetControlCenterMonitor();
        var version = Interlocked.Increment(ref _controlCommandVersion);
        var displayId = ResolveControlBrightnessDisplay(widget);
        await Task.Run(async () =>
        {
            ControlCenterSnapshot? snapshot = null; var error = "";
            await ControlIoGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed || IsPaused) return;
                source.BrightnessDisplayId = displayId;
                if (!await command(source).ConfigureAwait(false)) error = failure;
                snapshot = await source.ReadAsync().ConfigureAwait(false);
            }
            catch (Exception exception) { error = failure + "：" + exception.Message; }
            finally { ControlIoGate.Release(); }
            PublishControlResult(widget, version, snapshot, error);
        }).ConfigureAwait(false);
    }

    public Task ToggleControlAsync(DesktopWidget widget, ControlCenterToggle toggle, bool enabled) =>
        widget.Kind != DesktopWidgetKind.ControlCenter ? Task.CompletedTask : RunControlCommandAsync(widget,
            source => toggle == ControlCenterToggle.NightLight ? Task.FromResult(source.SetNightLight(enabled)) : source.SetRadioAsync(toggle, enabled),
            "无法切换" + ControlCenterPresentation.ToggleLabel(toggle));

    private static string ResolveControlBrightnessDisplay(DesktopWidget widget) =>
        string.IsNullOrEmpty(widget.ControlBrightnessDisplayId) ? widget.MonitorId : widget.ControlBrightnessDisplayId;

    public IReadOnlyList<(string Id, string Label)> ControlBrightnessDisplays => Monitors.Select((monitor, index) =>
        (monitor.Id, $"显示器 {index + 1}" + (string.IsNullOrWhiteSpace(monitor.ModelName) ? "" : " · " + monitor.ModelName) +
            $" · {monitor.PixelBounds.Width:0} × {monitor.PixelBounds.Height:0}" + (monitor.IsPrimary ? " · 主屏" : ""))).ToArray();

    public string ControlBrightnessDisplayLabel(DesktopWidget widget) =>
        ControlBrightnessDisplays.FirstOrDefault(choice => choice.Id == ResolveControlBrightnessDisplay(widget)).Label ?? "所选显示器已断开";

    public Task SelectControlBrightnessDisplayAsync(DesktopWidget widget, string displayId)
    {
        if (_disposed || widget.Kind != DesktopWidgetKind.ControlCenter || !State.Widgets.Contains(widget) || !Monitors.Any(m => m.Id == displayId)) return Task.CompletedTask;
        widget.ControlBrightnessDisplayId = displayId;
        lock (ControlQueueGate)
        {
            _pendingControlBrightness = null;
            _pendingControlDisplayId = displayId;
            Interlocked.Increment(ref _controlCommandVersion);
        }
        if (widget.Controls is { } current) widget.Controls = current with { BrightnessPercent = null };
        WidgetChanged(widget);
        return RunControlCommandAsync(widget, _ => Task.FromResult(true), "无法读取显示器亮度");
    }

    public Task SetControlOutputAsync(string deviceId) => string.IsNullOrEmpty(deviceId) || ControlCenterWidget is not { } widget
        ? Task.CompletedTask : RunControlCommandAsync(widget, async source =>
        {
            var changed = source.SetOutputDevice(deviceId);
            if (changed) await Task.Delay(80).ConfigureAwait(false);
            return changed;
        }, "无法切换输出设备");

    public Task SendControlMediaCommandAsync(ControlCenterMediaCommand command) => ControlCenterWidget is not { } widget
        ? Task.CompletedTask : RunControlCommandAsync(widget, async _ =>
        {
            var changed = await WidgetControlCenterMonitor.MediaCommandAsync(command).ConfigureAwait(false);
            if (changed) await Task.Delay(150).ConfigureAwait(false);
            return changed;
        }, "无法控制媒体播放");

    public Task SeekControlMediaAsync(DesktopWidget widget, ControlCenterMedia media, TimeSpan position)
    {
        if (_disposed || IsPaused || !State.Widgets.Contains(widget) || widget.Kind != DesktopWidgetKind.ControlCenter ||
            !media.CanSeek || media.Duration <= TimeSpan.Zero || string.IsNullOrEmpty(media.TrackId) ||
            widget.Controls?.Media?.TrackId != media.TrackId) return Task.CompletedTask;
        var target = ControlCenterPresentation.ClampMediaPosition(media, position);
        widget.Controls = widget.Controls with { Media = media with { Position = target, CapturedAt = DateTimeOffset.Now } };
        NotifyMonitorFrame();
        return RunControlCommandAsync(widget, async source =>
        {
            var changed = await source.SeekMediaAsync(media, target).ConfigureAwait(false);
            if (changed) await Task.Delay(150).ConfigureAwait(false);
            return changed;
        }, "无法跳转播放进度");
    }
}
