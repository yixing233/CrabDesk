namespace CrabDesk.Core;

/// <summary>
/// Serializes a user-visible AI organization operation and exposes one
/// cancellation handle for every entry point that can start it.
/// </summary>
public sealed class AiOrganizationOperationGate : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private CancellationTokenSource? _activeCancellation;
    private bool _disposed;

    public event EventHandler? Changed;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _activeCancellation is not null;
            }
        }
    }

    public void Cancel()
    {
        lock (_sync)
        {
            _activeCancellation?.Cancel();
        }
    }

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("AI 整理正在运行，请等待当前操作完成。");
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_sync)
        {
            _activeCancellation = linkedCancellation;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            return await operation(linkedCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeCancellation, linkedCancellation))
                {
                    _activeCancellation = null;
                }
            }
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Cancel();
    }
}
