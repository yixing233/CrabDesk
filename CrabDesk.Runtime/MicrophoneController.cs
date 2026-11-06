using CrabDesk.Core;
using CrabDesk.Native;

namespace CrabDesk.Runtime;

internal sealed class MicrophoneController(IMicrophone source) : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly Dictionary<string, Change> _pending = [];
    private MicrophoneSnapshot _snapshot = MicrophoneSnapshot.Empty;
    private MicrophoneSnapshot _confirmed = MicrophoneSnapshot.Empty;
    private string? _pendingDevice;
    private Task _writeTask = Task.CompletedTask;
    private bool _writing, _refreshing, _disposed, _hasSnapshot;
    private long _version;
    private string _error = "";
    private readonly record struct Change(double? Volume, bool? Muted);

    public event Action? Changed;
    public MicrophoneSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public bool HasSnapshot { get { lock (_gate) return _hasSnapshot; } }
    public string Error { get { lock (_gate) return _error; } }
    internal Task PendingWrites { get { lock (_gate) return _writeTask; } }

    public async Task RefreshAsync()
    {
        long version;
        lock (_gate)
        {
            if (_disposed || _writing || _refreshing) return;
            _refreshing = true;
            version = _version;
        }
        MicrophoneSnapshot? result = null;
        var error = "";
        await _io.WaitAsync().ConfigureAwait(false);
        try { result = await Task.Run(source.Read).ConfigureAwait(false); }
        catch (Exception) { error = "无法读取麦克风，请稍后重试"; }
        finally { _io.Release(); }
        var publish = false;
        lock (_gate)
        {
            _refreshing = false;
            if (!_disposed && version == _version && !_writing)
            {
                if (result is not null) _snapshot = _confirmed = result;
                _hasSnapshot = true;
                if (error.Length > 0) _error = error;
                publish = true;
            }
        }
        if (publish) Changed?.Invoke();
    }

    public void SetDevice(string deviceId)
    {
        lock (_gate)
        {
            if (_disposed || _snapshot.DeviceId == deviceId || _snapshot.Devices.FirstOrDefault(device => device.Id == deviceId) is not { } selected) return;
            _pendingDevice = deviceId;
            _snapshot = _snapshot with { DeviceId = deviceId, DeviceName = selected.Name, VolumePercent = null, Muted = null, PeakPercent = null };
            StartWrite();
        }
        Changed?.Invoke();
    }

    public void SetVolume(double percent) => Queue(ControlCenterPresentation.ClampPercent(percent), null);
    public void SetMuted(bool muted) => Queue(null, muted);

    private void Queue(double? volume, bool? muted)
    {
        lock (_gate)
        {
            if (_disposed || string.IsNullOrEmpty(_snapshot.DeviceId) ||
                volume is not null && _snapshot.VolumePercent is null || muted is not null && _snapshot.Muted is null) return;
            var previous = _pending.GetValueOrDefault(_snapshot.DeviceId);
            _pending[_snapshot.DeviceId] = new(volume ?? previous.Volume, muted ?? previous.Muted);
            _snapshot = _snapshot with { VolumePercent = volume ?? _snapshot.VolumePercent, Muted = muted ?? _snapshot.Muted };
            StartWrite();
        }
        Changed?.Invoke();
    }

    private void StartWrite()
    {
        _version++;
        _error = "";
        if (_writing) return;
        _writing = true;
        _writeTask = Task.Run(DrainAsync);
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            await Task.Delay(70).ConfigureAwait(false);
            KeyValuePair<string, Change>[] changes;
            string? selected;
            long version;
            lock (_gate)
            {
                if (_pending.Count == 0 && _pendingDevice is null) { _writing = false; return; }
                changes = _pending.ToArray();
                _pending.Clear();
                selected = _pendingDevice;
                _pendingDevice = null;
                version = _version;
            }
            var error = "";
            MicrophoneSnapshot? result = null;
            await _io.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var (device, change) in changes)
                {
                    if (change.Volume is { } volume && !source.SetVolume(device, volume)) error = "无法调整麦克风音量";
                    if (change.Muted is { } muted && !source.SetMuted(device, muted)) error = "无法切换麦克风静音状态";
                }
                if (selected is not null && !source.SetDevice(selected)) error = "无法切换输入设备";
                result = source.Read();
            }
            catch (Exception) { error = "麦克风设置失败，请重试"; }
            finally { _io.Release(); }
            var publish = false;
            lock (_gate)
            {
                if (!_disposed && version == _version)
                {
                    _snapshot = result ?? _confirmed;
                    if (result is not null) _confirmed = result;
                    _hasSnapshot = true;
                    _error = error;
                    publish = true;
                }
            }
            if (publish) Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; Changed = null; }
    }
}
