using System.Diagnostics;

namespace CrabDesk.Runtime;

/// <summary>
/// Separates the two delays a dispatched timer tick can suffer.
/// <paramref name="PoolIntervalMs"/> is the gap between consecutive thread-pool
/// callbacks (large when the pool is starved); <paramref name="DispatchMs"/> is
/// how long the enqueued callback waited for the UI thread (large only when the
/// UI thread itself is blocked).
/// </summary>
internal readonly record struct RuntimeTimerLatency(double PoolIntervalMs, double DispatchMs);

internal sealed class RuntimeTimer : IDisposable
{
    private readonly TimeSpan _interval;
    private readonly bool _repeating;
    private readonly Action<Action> _beginInvoke;
    private readonly Action _callback;
    private readonly string _name;
    private readonly Action<RuntimeTimerLatency>? _observeLatency;
    private Timer? _timer;
    private long _lastPoolTimestamp;

    internal RuntimeTimer(
        TimeSpan interval,
        bool repeating,
        Action<Action> beginInvoke,
        Action callback,
        string name = "timer",
        Action<RuntimeTimerLatency>? observeLatency = null)
    {
        _interval = interval;
        _repeating = repeating;
        _beginInvoke = beginInvoke;
        _callback = callback;
        _name = name;
        _observeLatency = observeLatency;
    }

    internal void Start()
    {
        Stop();
        _timer = new Timer(
            _ => Tick(),
            null,
            _interval,
            _repeating ? _interval : Timeout.InfiniteTimeSpan);
    }

    internal void Stop() => Interlocked.Exchange(ref _timer, null)?.Dispose();

    public void Dispose() => Stop();

    private void Tick()
    {
        var enqueuedAt = Stopwatch.GetTimestamp();
        var previousPoolTimestamp = Interlocked.Exchange(ref _lastPoolTimestamp, enqueuedAt);
        _beginInvoke(() =>
        {
            if (_observeLatency is not null)
            {
                _observeLatency(new RuntimeTimerLatency(
                    previousPoolTimestamp == 0
                        ? 0
                        : Stopwatch.GetElapsedTime(previousPoolTimestamp, enqueuedAt).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(enqueuedAt).TotalMilliseconds));
            }

            using var scope = UiThreadWatchdog.Enter(_name);
            _callback();
        });
    }
}
