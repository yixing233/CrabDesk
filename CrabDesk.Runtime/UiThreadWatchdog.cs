using System.Diagnostics;

namespace CrabDesk.Runtime;

/// <summary>
/// Watches the UI thread from a dedicated thread so a stall is still reported
/// when the thread pool is starved. Instrumented work leaves a breadcrumb, so a
/// report names what the UI thread was running; an empty breadcrumb means the
/// thread was blocked outside the managed work CrabDesk schedules itself.
/// </summary>
internal static class UiThreadWatchdog
{
    internal const string IdleScope = "idle";

    private static readonly TimeSpan StallThreshold = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(100);

    private static long _lastAliveTimestamp;
    private static long _scopeEnteredTimestamp;
    private static string _scope = IdleScope;
    private static int _scopeMessage;
    private static int _started;

    /// <summary>
    /// Records that the UI thread just executed instrumented work. Called from
    /// the UI thread only.
    /// </summary>
    internal static void ReportAlive() =>
        Volatile.Write(ref _lastAliveTimestamp, Stopwatch.GetTimestamp());

    /// <summary>
    /// Marks the UI thread as running <paramref name="name"/> until the
    /// returned scope is disposed. Cheap enough for every dispatched callback:
    /// two static writes on entry and two on exit.
    /// </summary>
    internal static ActivityScope Enter(string name)
    {
        var previousScope = _scope;
        var previousTimestamp = _scopeEnteredTimestamp;
        Volatile.Write(ref _scopeEnteredTimestamp, Stopwatch.GetTimestamp());
        Volatile.Write(ref _scope, name);
        return new ActivityScope(previousScope, previousTimestamp);
    }

    /// <summary>
    /// Same as <see cref="Enter"/> for window-message handling, which is the one
    /// UI-thread path no dispatch wrapper can see.
    /// </summary>
    internal static ActivityScope EnterWindowMessage(string window, int message)
    {
        Volatile.Write(ref _scopeMessage, message);
        return Enter(window);
    }

    internal static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _lastAliveTimestamp, Stopwatch.GetTimestamp());
        var thread = new Thread(Watch)
        {
            IsBackground = true,
            Name = "CrabDesk UI watchdog",
            Priority = ThreadPriority.AboveNormal
        };
        thread.Start();
    }

    private static void Watch()
    {
        var stallStartedAt = 0L;
        var stallScope = IdleScope;
        var stallScopeElapsed = TimeSpan.Zero;

        while (true)
        {
            Thread.Sleep(SampleInterval);
            var alive = Volatile.Read(ref _lastAliveTimestamp);
            if (alive == 0)
            {
                continue;
            }

            var now = Stopwatch.GetTimestamp();
            var quiet = Stopwatch.GetElapsedTime(alive, now);
            if (quiet >= StallThreshold)
            {
                if (stallStartedAt == 0)
                {
                    stallStartedAt = alive;
                    stallScope = Volatile.Read(ref _scope);
                    stallScopeElapsed = Stopwatch.GetElapsedTime(Volatile.Read(ref _scopeEnteredTimestamp), now);
                    DiagnosticLog.Info(
                        $"UI thread stall began scope={stallScope} " +
                        $"msg=0x{Volatile.Read(ref _scopeMessage):X4} " +
                        $"scopeElapsedMs={stallScopeElapsed.TotalMilliseconds:0} " +
                        $"quietMs={quiet.TotalMilliseconds:0}");
                }

                continue;
            }

            if (stallStartedAt == 0)
            {
                continue;
            }

            DiagnosticLog.Info(
                $"UI thread stall ended stalledMs={Stopwatch.GetElapsedTime(stallStartedAt, alive).TotalMilliseconds:0} " +
                $"scopeAtStall={stallScope} " +
                $"scopeElapsedAtStallMs={stallScopeElapsed.TotalMilliseconds:0} " +
                $"scopeNow={Volatile.Read(ref _scope)}");
            stallStartedAt = 0;
        }
    }

    internal readonly struct ActivityScope(string previousScope, long previousTimestamp) : IDisposable
    {
        public void Dispose()
        {
            Volatile.Write(ref _scopeEnteredTimestamp, previousTimestamp);
            Volatile.Write(ref _scope, previousScope);
        }
    }
}
