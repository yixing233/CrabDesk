using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

internal sealed class DesktopAnimationFrameClock : IDisposable
{
    // A WinForms timer is a USER32 timer, so a tick can only be delivered on a
    // system clock boundary — about every 15.625 ms. Asking for 16 ms therefore
    // misses the next boundary and waits for the one after it: measured here at
    // a 30.5 ms median with an 8/35 ms spread, i.e. half the intended frame rate
    // with visible jitter. 15 ms lands on a single boundary: a measured 15.8 ms
    // median, 64 frames a second, evenly paced. Going lower buys nothing because
    // the boundary is the floor. Do not "round it back up" to 16 for 60 FPS.
    internal const int IntervalMilliseconds = 15;
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
