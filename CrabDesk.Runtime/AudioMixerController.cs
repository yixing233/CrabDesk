using CrabDesk.Core;
using CrabDesk.Native;

namespace CrabDesk.Runtime;

internal sealed class AudioMixerController(IAudioMixer source) : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly Dictionary<Target, Change> _pending = [];
    private AudioMixerSnapshot _snapshot = AudioMixerSnapshot.Empty;
    private AudioMixerSnapshot _confirmed = AudioMixerSnapshot.Empty;
    private Task _writeTask = Task.CompletedTask;
    private bool _writing, _refreshing, _disposed, _hasSnapshot;
    private long _version;
    private string _error = "";

    private readonly record struct Target(string DeviceId, string? ApplicationId);
    private readonly record struct Change(double? Volume, bool? Muted);

    public event Action? Changed;
    public AudioMixerSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public string Error { get { lock (_gate) return _error; } }
    public bool HasSnapshot { get { lock (_gate) return _hasSnapshot; } }
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
        AudioMixerSnapshot? result = null;
        var error = "";
        await _io.WaitAsync().ConfigureAwait(false);
        try { result = await Task.Run(source.Read).ConfigureAwait(false); }
        catch (Exception) { error = "无法读取应用音量，请稍后重试"; }
        finally { _io.Release(); }
        var publish = false;
        lock (_gate)
        {
            _refreshing = false;
            if (!_disposed && version == _version && !_writing)
            {
                if (result is not null) _snapshot = _confirmed = result;
                _hasSnapshot = true;
                _error = error;
                publish = true;
            }
        }
        if (publish) Changed?.Invoke();
    }

    public void SetVolume(string? applicationId, double percent) =>
        Queue(applicationId, ControlCenterPresentation.ClampPercent(percent), false);

    public void SetMuted(string? applicationId, bool muted) => Queue(applicationId, null, muted);

    private void Queue(string? applicationId, double? volume, bool? muted)
    {
        lock (_gate)
        {
            if (_disposed || string.IsNullOrEmpty(_snapshot.DeviceId) ||
                applicationId is not null && !_snapshot.Applications.Any(app => app.Id == applicationId)) return;
            var target = new Target(_snapshot.DeviceId, applicationId);
            var previous = _pending.GetValueOrDefault(target);
            _pending[target] = new(volume ?? previous.Volume, muted ?? previous.Muted);
            _version++;
            _error = "";
            _snapshot = applicationId is null
                ? _snapshot with { VolumePercent = volume ?? _snapshot.VolumePercent, Muted = muted ?? _snapshot.Muted }
                : _snapshot with { Applications = _snapshot.Applications.Select(app => app.Id == applicationId
                    ? app with { VolumePercent = volume ?? app.VolumePercent, Muted = muted ?? app.Muted } : app).ToArray() };
            if (!_writing)
            {
                _writing = true;
                _writeTask = Task.Run(DrainAsync);
            }
        }
        Changed?.Invoke();
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            await Task.Delay(80).ConfigureAwait(false);
            KeyValuePair<Target, Change>[] changes;
            long version;
            lock (_gate)
            {
                if (_pending.Count == 0) { _writing = false; return; }
                changes = _pending.ToArray();
                _pending.Clear();
                version = _version;
            }
            var error = "";
            AudioMixerSnapshot? result = null;
            await _io.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var (target, change) in changes)
                {
                    var volumeWritten = change.Volume is not { } volume || source.SetVolume(target.DeviceId, target.ApplicationId, volume);
                    if (!volumeWritten) error = "无法调整音量，应用可能已经关闭";
                    if (change.Muted is { } muted && (muted || volumeWritten) && !source.SetMuted(target.DeviceId, target.ApplicationId, muted))
                        error = "无法切换静音状态";
                }
                result = source.Read();
            }
            catch (Exception) { error = "音量设置失败，请重试"; }
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
