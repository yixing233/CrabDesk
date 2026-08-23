using System;

namespace CrabDesk.Runtime;

internal static class BoxItemSearchFilter
{
    internal static bool MatchesDisplayName(string displayName, string? query)
    {
        var normalized = query?.Trim();
        return string.IsNullOrEmpty(normalized) ||
            displayName.Contains(normalized, StringComparison.CurrentCultureIgnoreCase);
    }
}
