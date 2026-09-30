namespace CrabDesk.Core;

/// <summary>
/// The cached "latest release" is only valid for the build that computed it.
/// Build <c>20260924.01</c> ranked date revisions above <c>1.0.0</c>, so it
/// stored <c>20260924.02</c> as the newest release while <c>1.0.0</c> was live.
/// After upgrading, the cached ETag still matched and GitHub answered
/// <c>304 Not Modified</c>, so the fixed comparison never ran and the user was
/// told they were already up to date forever. Discarding the cache whenever the
/// application version changes forces one real release listing per upgrade.
/// </summary>
public static class UpdateCachePolicy
{
    public static bool IsCacheStale(string? cacheAppVersion, string? currentVersion) =>
        !string.Equals(
            cacheAppVersion?.Trim() ?? string.Empty,
            currentVersion?.Trim() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Clears the cached release fields when they were produced by another
    /// application version. Returns <c>true</c> when anything was cleared.
    /// </summary>
    public static bool InvalidateIfStale(UpdateSettings settings, string? currentVersion)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!IsCacheStale(settings.CacheAppVersion, currentVersion))
        {
            return false;
        }

        var hadCachedRelease =
            !string.IsNullOrWhiteSpace(settings.CachedETag) ||
            !string.IsNullOrWhiteSpace(settings.LatestKnownVersion) ||
            !string.IsNullOrWhiteSpace(settings.CachedReleaseName);
        var hadResultStatus =
            settings.LastStatus is UpdateCheckStatus.UpToDate or UpdateCheckStatus.UpdateAvailable;
        settings.CachedETag = string.Empty;
        settings.LatestKnownVersion = string.Empty;
        settings.CachedReleaseName = string.Empty;
        settings.CachedPublishedAt = null;
        settings.CachedReleaseNotes = string.Empty;
        settings.CachedReleasePageUrl = string.Empty;
        settings.CachedInstallerUrl = string.Empty;
        settings.CachedSha256Url = string.Empty;
        settings.CachedIsPrerelease = false;
        settings.CacheAppVersion = currentVersion?.Trim() ?? string.Empty;

        // The last result belonged to the old cache: invite a fresh manual check
        // instead of replaying "already up to date" computed under old rules.
        // Dropping the timestamp also releases the startup check from its
        // six-hour throttle, which otherwise measures age against a result that
        // no longer exists.
        if (hadResultStatus)
        {
            settings.LastStatus = UpdateCheckStatus.NotChecked;
            settings.LastMessage = string.Empty;
        }
        if (hadCachedRelease || hadResultStatus)
        {
            settings.LastCheckedAt = null;
        }
        return hadCachedRelease || hadResultStatus;
    }
}
