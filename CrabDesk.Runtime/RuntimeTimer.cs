namespace CrabDesk.Runtime;

internal sealed class RuntimeTimer : IDisposable
{
    private readonly TimeSpan _interval;
    private readonly bool _repeating;
    private readonly Action<Action> _beginInvoke;
    private readonly Action _callback;
    private Timer? _timer;

    internal RuntimeTimer(TimeSpan interval, bool repeating, Action<Action> beginInvoke, Action callback)
    {
        _interval = interval;
        _repeating = repeating;
        _beginInvoke = beginInvoke;
        _callback = callback;
    }

    internal void Start()
    {
        Stop();
        _timer = new Timer(
            _ => _beginInvoke(_callback),
            null,
            _interval,
            _repeating ? _interval : Timeout.InfiniteTimeSpan);
    }

    internal void Stop() => Interlocked.Exchange(ref _timer, null)?.Dispose();

    public void Dispose() => Stop();
}
