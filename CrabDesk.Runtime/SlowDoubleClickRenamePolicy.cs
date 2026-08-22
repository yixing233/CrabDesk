namespace CrabDesk.Runtime;

internal static class SlowDoubleClickRenamePolicy
{
    internal const int RenameLimitMilliseconds = 900;

    internal static bool IsSlowDoubleClick(
        string? previousItemKey,
        DateTime previousClickUtc,
        string currentItemKey,
        DateTime nowUtc,
        int systemDoubleClickTimeMilliseconds)
    {
        var elapsed = (nowUtc - previousClickUtc).TotalMilliseconds;
        return string.Equals(
                previousItemKey,
                currentItemKey,
                StringComparison.OrdinalIgnoreCase) &&
            elapsed > systemDoubleClickTimeMilliseconds &&
            elapsed < RenameLimitMilliseconds;
    }
}
