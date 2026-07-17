using System.Net;
using System.Security.Cryptography;
using System.Text;
using CrabDesk.Core;
using CrabDesk.Native;

namespace CrabDesk.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public async Task MissingRepositoryDoesNotIssueNetworkRequest()
    {
        var called = false;
        using var client = new HttpClient(new StubHandler(_ =>
        {
            called = true;
            return JsonResponse("{}");
        })) { BaseAddress = new Uri("https://api.github.test") };
        using var service = new GitHubUpdateService(client);
        var request = Request() with { RepositoryOwner = string.Empty };

        var result = await service.CheckAsync(request);

        Assert.Equal(UpdateCheckStatus.NotConfigured, result.Status);
        Assert.False(called);
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.2", 1)]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("1.2.3-beta.2", "1.2.3-beta.10", -1)]
    [InlineData("1.2.3", "1.2.3-rc.1", 1)]
    [InlineData("1.2.3-alpha", "1.2.3-beta", -1)]
    public void SemanticVersionsCompareAccordingToSemver(string left, string right, int expected)
    {
        Assert.True(SemanticVersion.TryParse(left, out var leftVersion));
        Assert.True(SemanticVersion.TryParse(right, out var rightVersion));

        Assert.Equal(expected, Math.Sign(leftVersion.CompareTo(rightVersion)));
    }

    [Fact]
    public async Task StableChannelUsesLatestReleaseAndSelectsFixedAssets()
    {
        HttpRequestMessage? captured = null;
        using var client = new HttpClient(new StubHandler(request =>
        {
            captured = request;
            var response = JsonResponse("""
            {
              "tag_name": "v0.7.0",
              "name": "CrabDesk 0.7.0",
              "draft": false,
              "prerelease": false,
              "published_at": "2026-07-14T08:30:00Z",
              "body": "Release notes",
              "html_url": "https://github.com/acme/CrabDesk/releases/tag/v0.7.0",
              "assets": [
                { "name": "CrabDesk-Setup-x64.exe", "browser_download_url": "https://download/setup.exe" },
                { "name": "SHA256SUMS.txt", "browser_download_url": "https://download/sha256.txt" }
              ]
            }
            """);
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"release-7\"");
            return response;
        })) { BaseAddress = new Uri("https://api.github.test") };
        using var service = new GitHubUpdateService(client);

        var result = await service.CheckAsync(Request());

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("0.7.0", result.LatestVersion);
        Assert.Equal("https://download/setup.exe", result.InstallerUrl);
        Assert.Equal("https://download/sha256.txt", result.Sha256Url);
        Assert.Equal("\"release-7\"", result.ETag);
        Assert.Equal("/repos/acme/CrabDesk/releases/latest", captured!.RequestUri!.PathAndQuery);
        Assert.Contains("CrabDesk/0.6.0", captured.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task PreviewChannelChoosesHighestNonDraftSemanticVersion()
    {
        using var client = new HttpClient(new StubHandler(_ => JsonResponse("""
        [
          { "tag_name": "v0.7.0-beta.2", "name": "Beta 2", "draft": false, "prerelease": true, "assets": [] },
          { "tag_name": "v0.6.5", "name": "Stable", "draft": false, "prerelease": false, "assets": [] },
          { "tag_name": "v0.8.0-alpha.1", "name": "Draft", "draft": true, "prerelease": true, "assets": [] }
        ]
        """))) { BaseAddress = new Uri("https://api.github.test") };
        using var service = new GitHubUpdateService(client);

        var result = await service.CheckAsync(Request(UpdateChannel.Preview));

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("0.7.0-beta.2", result.LatestVersion);
        Assert.True(result.IsPrerelease);
    }

    [Fact]
    public async Task NotModifiedResponseUsesCachedReleaseAndSendsEtag()
    {
        string? ifNoneMatch = null;
        using var client = new HttpClient(new StubHandler(request =>
        {
            ifNoneMatch = request.Headers.IfNoneMatch.Single().ToString();
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        })) { BaseAddress = new Uri("https://api.github.test") };
        using var service = new GitHubUpdateService(client);
        var request = Request() with
        {
            CachedETag = "\"cached-tag\"",
            CachedLatestVersion = "0.6.1",
            CachedReleaseName = "Cached release",
            CachedReleasePageUrl = "https://github.test/release"
        };

        var result = await service.CheckAsync(request);

        Assert.Equal("\"cached-tag\"", ifNoneMatch);
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("Cached release", result.ReleaseName);
    }

    [Fact]
    public async Task RateLimitAndOfflineHaveExplicitNonThrowingStates()
    {
        using var rateClient = new HttpClient(new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            return response;
        })) { BaseAddress = new Uri("https://api.github.test") };
        using var rateService = new GitHubUpdateService(rateClient);
        var rateResult = await rateService.CheckAsync(Request());

        using var offlineClient = new HttpClient(new StubHandler(_ => throw new HttpRequestException("offline")))
        {
            BaseAddress = new Uri("https://api.github.test")
        };
        using var offlineService = new GitHubUpdateService(offlineClient);
        var offlineResult = await offlineService.CheckAsync(Request());

        Assert.Equal(UpdateCheckStatus.RateLimited, rateResult.Status);
        Assert.Equal(UpdateCheckStatus.Offline, offlineResult.Status);
    }

    [Fact]
    public async Task DownloadVerifiesSha256BeforePublishingInstaller()
    {
        var installer = Encoding.UTF8.GetBytes("signed installer fixture");
        var hash = Convert.ToHexString(SHA256.HashData(installer)).ToLowerInvariant();
        using var client = new HttpClient(new StubHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{hash}  CrabDesk-Setup-x64.exe\n", Encoding.ASCII)
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(installer)
                }));
        using var service = new GitHubUpdateService(client);
        var root = Path.Combine(Path.GetTempPath(), "CrabDesk.UpdateTests", Guid.NewGuid().ToString("N"));
        try
        {
            var result = await service.DownloadAsync(new UpdateDownloadRequest(
                "https://download.test/CrabDesk-Setup-x64.exe",
                "https://download.test/SHA256SUMS.txt",
                "0.7.0",
                root));

            Assert.True(result.Success, result.Message);
            Assert.Equal(hash, result.Sha256);
            Assert.Equal(installer, await File.ReadAllBytesAsync(result.InstallerPath));
            Assert.False(File.Exists(result.InstallerPath + ".part"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DownloadRejectsHashMismatchAndRemovesPartialFile()
    {
        var installer = Encoding.UTF8.GetBytes("tampered installer fixture");
        using var client = new HttpClient(new StubHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{new string('0', 64)}  CrabDesk-Setup-x64.exe\n", Encoding.ASCII)
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(installer)
                }));
        using var service = new GitHubUpdateService(client);
        var root = Path.Combine(Path.GetTempPath(), "CrabDesk.UpdateTests", Guid.NewGuid().ToString("N"));
        try
        {
            var result = await service.DownloadAsync(new UpdateDownloadRequest(
                "https://download.test/CrabDesk-Setup-x64.exe",
                "https://download.test/SHA256SUMS.txt",
                "0.7.0",
                root));

            Assert.False(result.Success);
            Assert.Contains("SHA-256", result.Message);
            Assert.Empty(Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(root, "*.part", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void AuthenticodeVerifierRejectsUnsignedAssembly()
    {
        var result = AuthenticodeVerifier.Verify(typeof(UpdateServiceTests).Assembly.Location);

        Assert.False(result.IsTrusted);
        Assert.NotEmpty(result.Message);
    }

    private static UpdateCheckRequest Request(UpdateChannel channel = UpdateChannel.Stable) => new(
        "acme",
        "CrabDesk",
        "0.6.0",
        channel,
        string.Empty,
        string.Empty,
        string.Empty,
        null,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        false);

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
}
