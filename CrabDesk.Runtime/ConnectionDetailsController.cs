using System;
using System.Threading;
using System.Threading.Tasks;
using CrabDesk.Core;
using CrabDesk.Native;

namespace CrabDesk.Runtime;

/// <summary>
/// Serializes a connection card's reads and commands. The caller's UI context
/// is retained because Windows requests radio and Wi-Fi consent on that thread.
/// </summary>
internal sealed class ConnectionDetailsController(IControlConnectionService source) : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private ControlConnectionSnapshot _snapshot = ControlConnectionSnapshot.Empty;
    private bool _busy, _refreshing, _hasSnapshot, _disposed, _sourceDisposed, _cancelling;
    private TaskCompletionSource? _refreshCompletion;
    private string _error = "";

    public ControlConnectionKind Kind { get; } = source.Kind;
    public ControlConnectionSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public bool Busy { get { lock (_gate) return _busy; } }
    public bool Refreshing { get { lock (_gate) return _refreshing; } }
    public Task WaitForRefreshAsync() { lock (_gate) return _refreshCompletion?.Task ?? Task.CompletedTask; }
    public bool HasSnapshot { get { lock (_gate) return _hasSnapshot; } }
    public string Error { get { lock (_gate) return _error; } }
    public event Action? Changed;

    public async Task RefreshAsync(bool scan = false)
    {
        if (!TryBegin(out var token, out _, refreshing: true)) return;
        try
        {
            NotifyChanged();
            token.ThrowIfCancellationRequested();
            var snapshot = await source.ReadAsync(scan, token);
            lock (_gate)
            {
                if (_disposed) return;
                _snapshot = snapshot;
                _hasSnapshot = true;
                _error = "";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            lock (_gate)
            {
                if (!_disposed) _error = "无法读取连接信息，请稍后重试。";
            }
        }
        finally { Finish(); }
    }

    public Task<ControlOperationResult> SetEnabledAsync(bool enabled) =>
        ExecuteAsync(token => source.SetEnabledAsync(enabled, token));

    public Task<ControlOperationResult> ConnectAsync(string id, string? password) =>
        ExecuteAsync(token => source.ConnectAsync(id, password, token));

    public Task<ControlOperationResult> DisconnectAsync(string id) =>
        ExecuteAsync(token => source.DisconnectAsync(id, token));

    private async Task<ControlOperationResult> ExecuteAsync(Func<CancellationToken, Task<ControlOperationResult>> command)
    {
        if (!TryBegin(out var token, out var rejection)) return new(false, rejection);
        try
        {
            NotifyChanged();
            token.ThrowIfCancellationRequested();
            ControlOperationResult result;
            try { result = await command(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { result = new(false, "连接操作失败，请稍后重试或使用系统设置。"); }

            ControlConnectionSnapshot? snapshot = null;
            var readError = "";
            if (!token.IsCancellationRequested)
            {
                try { snapshot = await source.ReadAsync(false, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception) { readError = "操作已结束，但无法更新连接状态，请刷新。"; }
            }

            lock (_gate)
            {
                if (!_disposed)
                {
                    if (snapshot is not null)
                    {
                        _snapshot = snapshot;
                        _hasSnapshot = true;
                    }
                    _error = result.Succeeded ? readError : result.Error;
                }
            }
            // Preserve PasswordRequired and the service's exact operation result,
            // even if the subsequent passive read reports a separate failure.
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new(false, "连接卡片已关闭，操作已取消。");
        }
        finally { Finish(); }
    }

    private bool TryBegin(out CancellationToken token, out string rejection, bool refreshing = false)
    {
        lock (_gate)
        {
            token = default;
            rejection = _disposed ? "连接卡片已关闭。" : "正在处理连接，请稍候。";
            if (_disposed || _busy) return false;
            token = _lifetime.Token;
            _busy = true;
            _refreshing = refreshing;
            if (refreshing) _refreshCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            else _error = "";
            return true;
        }
    }

    private void Finish()
    {
        bool release;
        TaskCompletionSource? refresh;
        lock (_gate)
        {
            _busy = false;
            _refreshing = false;
            refresh = _refreshCompletion;
            _refreshCompletion = null;
            release = TakeDisposal();
        }
        try
        {
            if (release) DisposeSource();
            else NotifyChanged();
        }
        finally { refresh?.TrySetResult(); }
    }

    private void NotifyChanged()
    {
        // Disposal and notification share the gate, so a late continuation can
        // never invoke a previously captured subscriber after Dispose returns.
        lock (_gate)
        {
            if (!_disposed) Changed?.Invoke();
        }
    }

    // Must hold _gate. Disposal waits for the complete in-flight command/read
    // and for cancellation callbacks to finish before releasing the source.
    private bool TakeDisposal()
    {
        if (!_disposed || _busy || _cancelling || _sourceDisposed) return false;
        _sourceDisposed = true;
        return true;
    }

    private void DisposeSource()
    {
        try { source.Dispose(); }
        finally { _lifetime.Dispose(); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _cancelling = true;
            Changed = null;
        }
        try { _lifetime.Cancel(); }
        finally
        {
            bool release;
            lock (_gate)
            {
                _cancelling = false;
                release = TakeDisposal();
            }
            if (release) DisposeSource();
        }
    }
}
