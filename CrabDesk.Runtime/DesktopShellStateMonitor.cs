using CrabDesk.Native;

namespace CrabDesk.Runtime;

internal sealed record DesktopShellSnapshot(
    DesktopIconViewState ViewState,
    string SystemIconVisibilitySignature)
{
    internal string Signature => $"{ViewState.Signature}\u001F{SystemIconVisibilitySignature}";
}

/// <summary>
/// Keeps Explorer/registry polling off the WinForms dispatcher. The callback
/// publishes immutable snapshots only when either observed signature changes.
/// </summary>
internal sealed class DesktopShellStateMonitor : IAsyncDisposable, IDisposable
{
    private readonly Func<DesktopShellSnapshot> _readSnapshot;
    private readonly Action<DesktopShellSnapshot> _publish;
    private readonly TimeSpan _interval;
    private readonly ManualResetEvent _stopSignal = new(false);
    private readonly object _sync = new();
    private Thread? _thread;
    private bool _disposed;

    internal DesktopShellStateMonitor(
        Func<DesktopShellSnapshot> readSnapshot,
        Action<DesktopShellSnapshot> publish,
        TimeSpan interval)
    {
        _readSnapshot = readSnapshot;
        _publish = publish;
        _interval = interval <= TimeSpan.Zero ? TimeSpan.FromSeconds(2) : interval;
    }

    internal void Start()
    {
        lock (_sync)
        {
            if (_disposed || _thread is not null)
            {
                return;
            }

            _stopSignal.Reset();
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "CrabDesk Shell State Monitor"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
    }

    internal Task StopAsync()
    {
        Thread? thread;
        lock (_sync)
        {
            thread = _thread;
            _thread = null;
            _stopSignal.Set();
        }

        return thread is null || thread == Thread.CurrentThread
            ? Task.CompletedTask
            : Task.Run(() => thread.Join(TimeSpan.FromSeconds(2)));
    }

    private void Run()
    {
        string? lastSignature = null;
        while (!_stopSignal.WaitOne(TimeSpan.Zero))
        {
            try
            {
                var snapshot = _readSnapshot();
                if (!string.Equals(lastSignature, snapshot.Signature, StringComparison.Ordinal))
                {
                    lastSignature = snapshot.Signature;
                    _publish(snapshot);
                }
            }
            catch (Exception exception)
            {
                DiagnosticLog.Verbose($"Shell state monitor sample failed: {exception.Message}");
            }

            _stopSignal.WaitOne(_interval);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        StopAsync().GetAwaiter().GetResult();
        _stopSignal.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _stopSignal.Dispose();
    }
}
