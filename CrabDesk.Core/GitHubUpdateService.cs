using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrabDesk.Core;

public sealed class GitHubUpdateService : IUpdateService
{
    private const string LegacyInstallerAssetName = "CrabDesk-Setup-x64.exe";
    private const string WebInstallerAssetName = "CrabDesk-Setup-Web-x64.exe";
    private const string FullInstallerAssetName = "CrabDesk-Setup-Full-x64.exe";
    private const string Sha256AssetName = "SHA256SUMS.txt";
    private const long MaximumInstallerBytes = 512L * 1024 * 1024;
    private const int MaximumChecksumBytes = 1024 * 1024;
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
        // Use one release stream for all users. GitHub's /releases/latest
        // excludes prereleases and returns 404 when a repository has only a
        // prerelease, which made the update experience depend on a hidden
        // channel setting.
        var endpoint = $"/repos/{owner}/{repository}/releases?per_page=20";
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
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Failed,
                    request.CurrentVersion,
                    Message: "GitHub 中暂无可用的 Release");
            }
            if ((response.StatusCode == HttpStatusCode.Forbidden ||
                 response.StatusCode == HttpStatusCode.TooManyRequests) &&
                (!response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) ||
                 remaining.FirstOrDefault() == "0"))
            {
                var retryMessage = response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues) &&
                                   long.TryParse(resetValues.FirstOrDefault(), out var resetSeconds)
                    ? $"，预计 {DateTimeOffset.FromUnixTimeSeconds(resetSeconds).ToLocalTime():HH:mm} 后恢复"
                    : "，请稍后重试";
                return new UpdateCheckResult(
                    UpdateCheckStatus.RateLimited,
                    request.CurrentVersion,
                    Message: $"GitHub API 请求频率已达上限{retryMessage}");
            }
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Failed,
                    request.CurrentVersion,
                    Message: $"GitHub 返回 HTTP {(int)response.StatusCode}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var releases = await ReadReleasesAsync(stream, cancellationToken).ConfigureAwait(false);
            var selected = releases
                .Where(release => !release.Draft)
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
            var installerAssetName = NormalizeInstallerAssetName(request.InstallerAssetName);
            var installerAsset = assets.FirstOrDefault(asset =>
                asset.Name.Equals(installerAssetName, StringComparison.OrdinalIgnoreCase));
            if (installerAsset is null && !installerAssetName.Equals(LegacyInstallerAssetName, StringComparison.OrdinalIgnoreCase))
            {
                installerAssetName = LegacyInstallerAssetName;
                installerAsset = assets.FirstOrDefault(asset =>
                    asset.Name.Equals(installerAssetName, StringComparison.OrdinalIgnoreCase));
            }
            var installerUrl = installerAsset?.BrowserDownloadUrl ?? string.Empty;
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
                response.Headers.ETag?.ToString() ?? string.Empty,
                InstallerAssetName: installerAssetName);
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

    public async Task<UpdateDownloadResult> DownloadAsync(
        UpdateDownloadRequest request,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetHttpsUri(request.InstallerUrl, out var installerUri) ||
            !TryGetHttpsUri(request.Sha256Url, out var sha256Uri))
        {
            return new UpdateDownloadResult(false, Message: "更新资源地址无效或不是 HTTPS 地址");
        }

        var version = SanitizeVersion(request.Version);
        if (version.Length == 0)
        {
            return new UpdateDownloadResult(false, Message: "更新版本号无效");
        }

        var destinationRoot = Path.GetFullPath(request.DestinationDirectory);
        var versionDirectory = Path.Combine(destinationRoot, version);
        var installerAssetName = NormalizeInstallerAssetName(request.InstallerAssetName);
        var installerPath = Path.Combine(versionDirectory, installerAssetName);
        var partialPath = installerPath + ".part";
        Directory.CreateDirectory(versionDirectory);

        try
        {
            progress?.Report(new UpdateDownloadProgress("正在获取校验文件"));
            var checksumText = await DownloadChecksumTextAsync(sha256Uri, cancellationToken)
                .ConfigureAwait(false);
            var expectedHash = FindExpectedHash(checksumText, installerAssetName);
            if (expectedHash is null)
            {
                return new UpdateDownloadResult(false, Message: $"{Sha256AssetName} 中缺少安装包校验值");
            }

            File.Delete(partialPath);
            using var httpRequest = CreateDownloadRequest(installerUri);
            using var response = await _client.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength;
            if (totalBytes is > MaximumInstallerBytes)
            {
                return new UpdateDownloadResult(false, Message: "更新安装包超过 512 MiB 限制");
            }

            progress?.Report(new UpdateDownloadProgress("正在下载安装包", 0, totalBytes));
            long received = 0;
            string actualHash;
            {
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                await using var destination = new FileStream(
                    partialPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[128 * 1024];
                long lastReported = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }
                    received += read;
                    if (received > MaximumInstallerBytes)
                    {
                        throw new InvalidDataException("更新安装包超过 512 MiB 限制");
                    }
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hasher.AppendData(buffer, 0, read);
                    if (received - lastReported >= 1024 * 1024 || received == totalBytes)
                    {
                        progress?.Report(new UpdateDownloadProgress("正在下载安装包", received, totalBytes));
                        lastReported = received;
                    }
                }
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                actualHash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }

            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(expectedHash),
                    Convert.FromHexString(actualHash)))
            {
                return new UpdateDownloadResult(false, Message: "安装包 SHA-256 校验失败");
            }

            File.Move(partialPath, installerPath, true);
            progress?.Report(new UpdateDownloadProgress("安装包下载完成", received, totalBytes ?? received));
            return new UpdateDownloadResult(true, installerPath, actualHash, Message: "安装包下载并校验完成");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or CryptographicException)
        {
            return new UpdateDownloadResult(false, Message: $"下载更新失败：{exception.Message}");
        }
        finally
        {
            TryDelete(partialPath);
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private async Task<string> DownloadChecksumTextAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = CreateDownloadRequest(uri);
        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumChecksumBytes)
        {
            throw new InvalidDataException("更新校验文件过大");
        }
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length > MaximumChecksumBytes)
        {
            throw new InvalidDataException("更新校验文件过大");
        }
        return Encoding.ASCII.GetString(bytes);
    }

    private static HttpRequestMessage CreateDownloadRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("CrabDesk", "Updater"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        return request;
    }

    private static bool TryGetHttpsUri(string value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var candidate) &&
            candidate.Scheme == Uri.UriSchemeHttps)
        {
            uri = candidate;
            return true;
        }
        uri = null!;
        return false;
    }

    private static string SanitizeVersion(string value)
    {
        var normalized = value.Trim().TrimStart('v', 'V');
        return normalized.Length <= 64 && normalized.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')
            ? normalized
            : string.Empty;
    }

    private static string? FindExpectedHash(string text, string fileName)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 66)
            {
                continue;
            }
            var separator = trimmed.IndexOfAny([' ', '\t']);
            if (separator != 64)
            {
                continue;
            }
            var hash = trimmed[..separator];
            var asset = trimmed[separator..].TrimStart().TrimStart('*').Trim();
            if (hash.All(Uri.IsHexDigit) &&
                asset.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                return hash.ToLowerInvariant();
            }
        }
        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task<IReadOnlyList<GitHubRelease>> ReadReleasesAsync(
        Stream stream,
        CancellationToken cancellationToken) =>
        await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];

    private static SemanticVersion? ParseVersion(string value) =>
        SemanticVersion.TryParse(value, out var version) ? version : null;

    private static string NormalizeInstallerAssetName(string value) =>
        value.Equals(WebInstallerAssetName, StringComparison.OrdinalIgnoreCase) ? WebInstallerAssetName :
        value.Equals(FullInstallerAssetName, StringComparison.OrdinalIgnoreCase) ? FullInstallerAssetName :
        LegacyInstallerAssetName;

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
            request.CachedETag,
            InstallerAssetName: NormalizeInstallerAssetName(request.InstallerAssetName));
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
