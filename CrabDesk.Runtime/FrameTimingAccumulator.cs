namespace CrabDesk.Runtime;

internal sealed class FrameTimingAccumulator
{
    private readonly object _sync = new();
    private readonly double[] _samples;
    private int _count;
    private int _next;

    internal FrameTimingAccumulator(int capacity)
    {
        _samples = new double[Math.Max(1, capacity)];
    }

    internal int Count
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    internal void Add(double milliseconds)
    {
        lock (_sync)
        {
            _samples[_next] = Math.Max(0, milliseconds);
            _next = (_next + 1) % _samples.Length;
            _count = Math.Min(_count + 1, _samples.Length);
        }
    }

    internal FrameTimingSnapshot SnapshotAndReset()
    {
        lock (_sync)
        {
            var values = _samples.Take(_count).Order().ToArray();
            var p95 = values.Length == 0
                ? 0
                : values[(int)Math.Ceiling(values.Length * .95) - 1];
            var snapshot = new FrameTimingSnapshot(values.Length, p95, values.LastOrDefault());
            _count = 0;
            _next = 0;
            return snapshot;
        }
    }
}

internal readonly record struct FrameTimingSnapshot(
    int Count,
    double P95Milliseconds,
    double MaxMilliseconds);
