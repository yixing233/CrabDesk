using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using CrabDesk.Core;

namespace CrabDesk.Native;

/// <summary>
/// Reads and commands the machine's quick settings.
///
/// Four unrelated stacks are involved because Windows offers no single API:
/// radios come from WinRT, volume and endpoint routing from Core Audio COM,
/// brightness from the WMI monitor provider, and media from the system transport
/// controls. Each part fails independently and reports null, so a machine
/// missing one capability still shows the others.
/// </summary>
public interface IWidgetControlCenterMonitor
{
    string BrightnessDisplayId { get; set; }
    ControlCenterSnapshot ReadFast();
    Task<ControlCenterSnapshot> ReadAsync(CancellationToken token = default);
    bool SetVolume(double percent);
    bool SetBrightness(double percent);
    bool SetMuted(bool muted);
    bool SetOutputDevice(string deviceId);
    /// <summary>Sets Windows system and application dark mode for the Night action.</summary>
    bool SetNightLight(bool enabled);
    Task<bool> SetRadioAsync(ControlCenterToggle toggle, bool enabled, CancellationToken token = default);
    Task<bool> SeekMediaAsync(ControlCenterMedia media, TimeSpan position, CancellationToken token = default);
}

public sealed partial class WidgetControlCenterMonitor : IWidgetControlCenterMonitor, IDisposable
{
    public string BrightnessDisplayId { get; set; } = "";
    private readonly object _gate = new();
    private bool _disposed;
    private bool _radioAccessRequested;
    private bool _radioAccessAllowed;

    /// <summary>
    /// Reads every control's current state. Async because the WinRT radio and
    /// media queries are async; callers run it off the UI thread.
    /// </summary>
    /// <summary>
    /// Partial read after a command: volume and brightness only. External display
    /// brightness can involve slow DDC/CI calls; run this on the hardware worker.
    /// </summary>
    public ControlCenterSnapshot ReadFast()
    {
        var audio = AudioSession.Read();
        return new ControlCenterSnapshot
        {
            CapturedAt = DateTimeOffset.Now,
            VolumePercent = audio.Volume,
            VolumeMuted = audio.Muted,
            OutputDeviceId = audio.DeviceId,
            OutputDeviceName = audio.DeviceName,
            BrightnessPercent = BrightnessControl.Read(BrightnessDisplayId).Percent
        };
    }

    public async Task<ControlCenterSnapshot> ReadAsync(CancellationToken token = default)
    {
        var snapshot = new ControlCenterSnapshot { CapturedAt = DateTimeOffset.Now };
        snapshot = snapshot with { WiFiEnabled = null, BluetoothEnabled = null };

        var radio = await ReadRadiosAsync(token).ConfigureAwait(false);
        snapshot = snapshot with { WiFiEnabled = radio.WiFi, BluetoothEnabled = radio.Bluetooth };
        snapshot = snapshot with
        {
            WiFiConnectionName = radio.WiFi is true ? WiFiConnectionService.ReadConnectedName() : "",
            BluetoothConnectionName = radio.Bluetooth is true ? await BluetoothConnectionService.ReadConnectedLabelAsync(token).ConfigureAwait(false) : ""
        };

        snapshot = snapshot with { NightLightEnabled = SystemThemeState.Read() };

        var audio = AudioSession.Read();
        snapshot = snapshot with
        {
            VolumePercent = audio.Volume,
            VolumeMuted = audio.Muted,
            OutputDeviceId = audio.DeviceId,
            OutputDeviceName = audio.DeviceName
        };

        var outputs = AudioSession.ReadOutputs();
        snapshot = snapshot with { OutputDevices = outputs };

        var brightness = BrightnessControl.Read(BrightnessDisplayId);
        snapshot = snapshot with { BrightnessPercent = brightness.Percent };

        var media = await ReadMediaAsync(token).ConfigureAwait(false);
        return snapshot with { Media = media };
    }

    private async Task<(bool? WiFi, bool? Bluetooth)> ReadRadiosAsync(CancellationToken token)
    {
        try
        {
            // Ask once per process; the answer is stable for the lifetime of the
            // app and the call is slow enough to matter at one-second cadence.
            if (!_radioAccessRequested)
            {
                _radioAccessRequested = true;
                var access = await global::Windows.Devices.Radios.Radio.RequestAccessAsync()
                    .AsTask(token).ConfigureAwait(false);
                _radioAccessAllowed = access == global::Windows.Devices.Radios.RadioAccessStatus.Allowed;
            }
            if (!_radioAccessAllowed) return (null, null);

            var radios = await global::Windows.Devices.Radios.Radio.GetRadiosAsync()
                .AsTask(token).ConfigureAwait(false);
            bool? wifi = null, bluetooth = null;
            foreach (var radio in radios)
            {
                var on = radio.State == global::Windows.Devices.Radios.RadioState.On;
                if (radio.Kind == global::Windows.Devices.Radios.RadioKind.WiFi) wifi = on;
                else if (radio.Kind == global::Windows.Devices.Radios.RadioKind.Bluetooth) bluetooth = on;
            }
            return (wifi, bluetooth);
        }
        catch (Exception)
        {
            // No radio, no permission, or WinRT unavailable.
            return (null, null);
        }
    }

    /// <summary>Switches a radio on or off. Returns false when it cannot be done.</summary>
    public async Task<bool> SetRadioAsync(ControlCenterToggle toggle, bool enabled, CancellationToken token = default)
    {
        try
        {
            if (!_radioAccessRequested)
            {
                await ReadRadiosAsync(token).ConfigureAwait(false);
            }
            if (!_radioAccessAllowed) return false;
            var kind = toggle == ControlCenterToggle.WiFi
                ? global::Windows.Devices.Radios.RadioKind.WiFi
                : global::Windows.Devices.Radios.RadioKind.Bluetooth;
            var radios = await global::Windows.Devices.Radios.Radio.GetRadiosAsync()
                .AsTask(token).ConfigureAwait(false);
            var radio = radios.FirstOrDefault(candidate => candidate.Kind == kind);
            if (radio is null) return false;
            var target = enabled
                ? global::Windows.Devices.Radios.RadioState.On
                : global::Windows.Devices.Radios.RadioState.Off;
            if (radio.State == target) return true;
            var result = await radio.SetStateAsync(target).AsTask(token).ConfigureAwait(false);
            return result == global::Windows.Devices.Radios.RadioAccessStatus.Allowed;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Runs a media transport command against the current session.</summary>
    public static async Task<bool> MediaCommandAsync(ControlCenterMediaCommand command, CancellationToken token = default)
    {
        try
        {
            var manager = await global::Windows.Media.Control
                .GlobalSystemMediaTransportControlsSessionManager.RequestAsync()
                .AsTask(token).ConfigureAwait(false);
            var session = manager.GetCurrentSession();
            if (session is null) return false;
            var transport = session;
            return command switch
            {
                ControlCenterMediaCommand.PlayPause => await transport.TryTogglePlayPauseAsync().AsTask(token).ConfigureAwait(false),
                ControlCenterMediaCommand.Next => await transport.TrySkipNextAsync().AsTask(token).ConfigureAwait(false),
                ControlCenterMediaCommand.Previous => await transport.TrySkipPreviousAsync().AsTask(token).ConfigureAwait(false),
                _ => false
            };
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Reads only the panel brightness; used to verify a write landed.</summary>
    public double? BrightnessReadAsync() => BrightnessControl.Read(BrightnessDisplayId).Percent;
    /// <summary>Reads only the endpoint volume; used to verify a write landed.</summary>
    public double? VolumeReadAsync() => AudioSession.Read().Volume;

    public bool SetVolume(double percent) => AudioSession.SetVolume(percent);
    public bool SetMuted(bool muted) => AudioSession.SetMuted(muted);
    public bool SetOutputDevice(string deviceId) => AudioSession.SetDefaultOutput(deviceId);
    public bool SetBrightness(double percent) => BrightnessControl.Write(percent, BrightnessDisplayId);
    public bool SetNightLight(bool enabled) => SystemThemeState.Write(enabled);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_mediaManager is { } manager) manager.CurrentSessionChanged -= OnCurrentMediaSessionChanged;
            _mediaManager = null;
            ClearMedia();
        }
    }
}

public enum ControlCenterMediaCommand { PlayPause, Next, Previous }
