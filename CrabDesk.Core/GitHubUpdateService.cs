using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrabDesk.Core;

public sealed class GitHubUpdateService : IUpdateService
{
    private const string InstallerAssetName = "CrabDesk-Setup-x64.exe";
    private const string Sha256AssetName = "SHA256SUMS.txt";
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public GitHubUpdateService(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com"),
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public async Task<UpdateCheckResult> CheckAsync(
        UpdateCheckRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.RepositoryOwner) ||
            string.IsNullOrWhiteSpace(request.RepositoryName))
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.NotConfigured,
                request.CurrentVersion,
                Message: "尚未配置 GitHub 发布仓库");
        }
        if (!SemanticVersion.TryParse(request.CurrentVersion, out var currentVersion))
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Failed,
                request.CurrentVersion,
                Message: "当前程序版本格式无效");
        }

        var owner = Uri.EscapeDataString(request.RepositoryOwner.Trim());
        var repository = Uri.EscapeDataString(request.RepositoryName.Trim());
        var endpoint = request.Channel == UpdateChannel.Stable
            ? $"/repos/{owner}/{repository}/releases/latest"
            : $"/repos/{owner}/{repository}/releases?per_page=20";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, endpoint);
        httpRequest.Headers.UserAgent.Add(new ProductInfoHeaderValue("CrabDesk", currentVersion.ToString()));
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        httpRequest.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrWhiteSpace(request.CachedETag) &&
            EntityTagHeaderValue.TryParse(request.CachedETag, out var cachedTag))
        {
            httpRequest.Headers.IfNoneMatch.Add(cachedTag);
        }

        try
        {
            using var response = await _client.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return BuildCachedResult(request, currentVersion);
            }
            if ((response.StatusCode == HttpStatusCode.Forbidden ||
                 response.StatusCode == HttpStatusCode.TooManyRequests) &&
                (!response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) ||
                 remaining.FirstOrDefault() == "0"))
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.RateLimited,
                    request.CurrentVersion,
                    Message: "GitHub API 请求频率已达上限，请稍后重试");
            }
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Failed,
                    request.CurrentVersion,
                    Message: $"GitHub 返回 HTTP {(int)response.StatusCode}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var releases = request.Channel == UpdateChannel.Stable
                ? await ReadStableReleaseAsync(stream, cancellationToken).ConfigureAwait(false)
                : await ReadPreviewReleasesAsync(stream, cancellationToken).ConfigureAwait(false);
            var selected = releases
                .Where(release => !release.Draft &&
                    (request.Channel == UpdateChannel.Preview || !release.Prerelease))
                .Select(release => new { Release = release, Parsed = ParseVersion(release.TagName) })
                .Where(candidate => candidate.Parsed is not null)
                .OrderByDescending(candidate => candidate.Parsed)
                .FirstOrDefault();
            if (selected is null)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Failed,
                    request.CurrentVersion,
                    Message: "GitHub Releases 中没有有效版本");
            }

            var release = selected.Release;
            var latestVersion = selected.Parsed!;
            var assets = release.Assets ?? [];
            var installerUrl = assets.FirstOrDefault(asset =>
                asset.Name.Equals(InstallerAssetName, StringComparison.OrdinalIgnoreCase))?.BrowserDownloadUrl ?? string.Empty;
            var sha256Url = assets.FirstOrDefault(asset =>
                asset.Name.Equals(Sha256AssetName, StringComparison.OrdinalIgnoreCase))?.BrowserDownloadUrl ?? string.Empty;
            return new UpdateCheckResult(
                latestVersion.CompareTo(currentVersion) > 0
                    ? UpdateCheckStatus.UpdateAvailable
                    : UpdateCheckStatus.UpToDate,
                currentVersion.ToString(),
                latestVersion.ToString(),
                string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name,
                release.PublishedAt,
                release.Body ?? string.Empty,
                release.HtmlUrl ?? string.Empty,
                installerUrl,
                sha256Url,
                release.Prerelease,
                response.Headers.ETag?.ToString() ?? string.Empty);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Offline,
                request.CurrentVersion,
                Message: "连接 GitHub 超时");
        }
        catch (HttpRequestException exception)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Offline,
                request.CurrentVersion,
                Message: exception.Message);
        }
        catch (JsonException exception)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Failed,
                request.CurrentVersion,
                Message: $"GitHub 响应格式无效：{exception.Message}");
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private static async Task<IReadOnlyList<GitHubRelease>> ReadStableReleaseAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return release is null ? [] : [release];
    }

    private static async Task<IReadOnlyList<GitHubRelease>> ReadPreviewReleasesAsync(
        Stream stream,
        CancellationToken cancellationToken) =>
        await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];

    private static SemanticVersion? ParseVersion(string value) =>
        SemanticVersion.TryParse(value, out var version) ? version : null;

    private static UpdateCheckResult BuildCachedResult(
        UpdateCheckRequest request,
        SemanticVersion currentVersion)
    {
        if (!SemanticVersion.TryParse(request.CachedLatestVersion, out var cachedVersion))
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Failed,
                currentVersion.ToString(),
                Message: "GitHub 返回未修改，但本地没有可用缓存");
        }
        return new UpdateCheckResult(
            cachedVersion.CompareTo(currentVersion) > 0
                ? UpdateCheckStatus.UpdateAvailable
                : UpdateCheckStatus.UpToDate,
            currentVersion.ToString(),
            cachedVersion.ToString(),
            request.CachedReleaseName,
            request.CachedPublishedAt,
            request.CachedReleaseNotes,
            request.CachedReleasePageUrl,
            request.CachedInstallerUrl,
            request.CachedSha256Url,
            request.CachedIsPrerelease,
            request.CachedETag);
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; init; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; init; }
    }
}
