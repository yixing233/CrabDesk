using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

internal sealed class DesktopAnimationFrameClock : IDisposable
{
    internal const int IntervalMilliseconds = 16;
    private readonly Forms.Timer _timer = new() { Interval = IntervalMilliseconds };

    internal DesktopAnimationFrameClock()
    {
        _timer.Tick += (_, _) => Frame?.Invoke(this, EventArgs.Empty);
    }

    internal event EventHandler? Frame;

    internal bool Enabled => _timer.Enabled;

    internal void RequestFrames()
    {
        if (!_timer.Enabled)
        {
            _timer.Start();
        }
    }

    internal void StopWhenIdle(bool hasActiveAnimation)
    {
        if (!hasActiveAnimation)
        {
            _timer.Stop();
        }
    }

    public void Dispose() => _timer.Dispose();
}
