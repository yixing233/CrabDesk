namespace CrabDesk.Native;

/// <summary>
/// Measures how long a window's thread takes to answer a null message, which is
/// how a stall is observed without injecting anything into that process.
/// </summary>
/// <remarks>
/// <c>SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG)</c> is the same primitive
/// CrabDesk already uses to check a busy Explorer thread before touching it
/// (<see cref="DesktopWindowTools.TryActivateDesktopInput"/>). It queues a
/// no-op message and waits for the target thread to pump it, so the round trip
/// is that thread's responsiveness; the timeout keeps the caller bounded when
/// the target is hung.
/// </remarks>
public static class DesktopStallProbe
{
    /// <summary>
    /// Returned when the window handle is unusable, so callers can tell "not
    /// measured" apart from "measured fast".
    /// </summary>
    public const int NotMeasured = -1;

    private const uint WmNull = 0x0000;
    private const uint DefaultTimeoutMs = 5000;

    public static int MeasureWorstResponseMs(
        IntPtr hwnd,
        int samples,
        int spacingMs,
        uint timeoutMs = DefaultTimeoutMs) =>
        MeasureWorstResponse(hwnd, samples, spacingMs, timeoutMs).WorstMs;

    /// <summary>
    /// Samples one window alone. Used for the idle baseline, where there is no
    /// second window whose reading has to line up with this one.
    /// </summary>
    public static StallSample MeasureWorstResponse(
        IntPtr hwnd,
        int samples,
        int spacingMs,
        uint timeoutMs = DefaultTimeoutMs)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return new StallSample(NotMeasured, 0, 0);
        }

        samples = Math.Max(1, samples);
        var worst = 0;
        var measured = 0;
        var timedOut = 0;
        for (var i = 0; i < samples; i++)
        {
            var (elapsedMs, ok) = SendNull(hwnd, timeoutMs);
            if (!ok)
            {
                timedOut++;
            }
            else
            {
                measured++;
                worst = Math.Max(worst, elapsedMs);
            }

            if (spacingMs > 0 && i < samples - 1)
            {
                Thread.Sleep(spacingMs);
            }
        }

        return new StallSample(measured == 0 ? NotMeasured : worst, measured, timedOut);
    }

    /// <summary>
    /// Samples two windows alternately, one right after the other, so both
    /// readings come from the same moment.
    /// </summary>
    /// <remarks>
    /// Measuring one window fully and then the other is not equivalent: the
    /// first window's stall may have ended by the time the second is sampled,
    /// which would make a blocked window look responsive. Interleaving is what
    /// lets the report claim "Explorer stalled while CrabDesk answered".
    /// </remarks>
    public static PairedStallSample MeasurePairedWorstResponse(
        IntPtr first,
        IntPtr second,
        int samples,
        int spacingMs,
        uint timeoutMs = DefaultTimeoutMs)
    {
        var firstUsable = first != IntPtr.Zero && NativeMethods.IsWindow(first);
        var secondUsable = second != IntPtr.Zero && NativeMethods.IsWindow(second);
        if (!firstUsable && !secondUsable)
        {
            return new PairedStallSample(
                new StallSample(NotMeasured, 0, 0),
                new StallSample(NotMeasured, 0, 0),
                0);
        }

        samples = Math.Max(1, samples);
        var firstWorst = 0;
        var firstMeasured = 0;
        var firstTimedOut = 0;
        var secondWorst = 0;
        var secondMeasured = 0;
        var secondTimedOut = 0;
        var pairedRounds = 0;
        for (var i = 0; i < samples; i++)
        {
            var firstOk = true;
            var secondOk = true;
            if (firstUsable)
            {
                var (elapsedMs, ok) = SendNull(first, timeoutMs);
                firstOk = ok;
                if (ok)
                {
                    firstMeasured++;
                    firstWorst = Math.Max(firstWorst, elapsedMs);
                }
                else
                {
                    firstTimedOut++;
                }
            }

            if (secondUsable)
            {
                var (elapsedMs, ok) = SendNull(second, timeoutMs);
                secondOk = ok;
                if (ok)
                {
                    secondMeasured++;
                    secondWorst = Math.Max(secondWorst, elapsedMs);
                }
                else
                {
                    secondTimedOut++;
                }
            }

            if (firstUsable && secondUsable && firstOk && secondOk)
            {
                pairedRounds++;
            }

            if (spacingMs > 0 && i < samples - 1)
            {
                Thread.Sleep(spacingMs);
            }
        }

        return new PairedStallSample(
            new StallSample(firstMeasured == 0 ? NotMeasured : firstWorst, firstMeasured, firstTimedOut),
            new StallSample(secondMeasured == 0 ? NotMeasured : secondWorst, secondMeasured, secondTimedOut),
            pairedRounds);
    }

    private static (int ElapsedMs, bool Ok) SendNull(IntPtr hwnd, uint timeoutMs)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = NativeMethods.SendMessageTimeout(
            hwnd,
            WmNull,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.SmtoAbortIfHung,
            timeoutMs,
            out _);
        var elapsedMs = (int)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return (elapsedMs, result != IntPtr.Zero);
    }
}

public readonly record struct StallSample(int WorstMs, int MeasuredCount, int TimedOutCount)
{
    public bool HasValue => WorstMs != DesktopStallProbe.NotMeasured;
}

public readonly record struct PairedStallSample(
    StallSample First,
    StallSample Second,
    int PairedRounds);
