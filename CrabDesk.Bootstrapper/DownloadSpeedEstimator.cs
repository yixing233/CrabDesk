namespace CrabDesk.Bootstrapper;

/// <summary>Calculates a stable download rate from cumulative byte samples.</summary>
internal sealed class DownloadSpeedEstimator
{
    private const double WindowSeconds = 2.0;
    private const double SmoothingFactor = 0.25;
    private readonly Queue<Sample> _samples = new();
    private double _smoothedSpeed;

    internal double CurrentSpeedBytesPerSecond => _smoothedSpeed;

    internal double AddSample(long bytesDownloaded, TimeSpan elapsed)
    {
        var seconds = Math.Max(0, elapsed.TotalSeconds);
        _samples.Enqueue(new Sample(bytesDownloaded, seconds));
        while (_samples.Count > 2 && seconds - _samples.Peek().Seconds > WindowSeconds)
        {
            _samples.Dequeue();
        }

        if (_samples.Count >= 2)
        {
            var first = _samples.Peek();
            var duration = seconds - first.Seconds;
            var bytes = bytesDownloaded - first.Bytes;
            if (duration > 0 && bytes >= 0)
            {
                var instantaneous = bytes / duration;
                _smoothedSpeed = _smoothedSpeed <= 0
                    ? instantaneous
                    : (_smoothedSpeed * (1 - SmoothingFactor)) + (instantaneous * SmoothingFactor);
            }
        }

        return _smoothedSpeed;
    }

    private readonly record struct Sample(long Bytes, double Seconds);
}
