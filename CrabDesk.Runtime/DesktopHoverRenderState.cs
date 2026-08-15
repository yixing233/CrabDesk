namespace CrabDesk.Runtime;

/// <summary>
/// Coalesces hover updates so a burst of pointer messages schedules at most
/// one render callback while retaining the latest item key.
/// </summary>
internal sealed class DesktopHoverRenderState
{
    private bool _pending;
    private string? _latestKey;

    internal bool Publish(string? key)
    {
        var shouldQueue = !_pending;
        _latestKey = key;
        _pending = true;
        return shouldQueue;
    }

    internal bool TryTake(out string? key)
    {
        if (!_pending)
        {
            key = null;
            return false;
        }

        _pending = false;
        key = _latestKey;
        _latestKey = null;
        return true;
    }
}
