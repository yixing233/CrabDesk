using CrabDesk.Native;

namespace CrabDesk.Tests;

public sealed class ShellIconProviderTests
{
    [Fact]
    public async Task CacheIsBoundedAndInvalidatesWhenFileChanges()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var completion = new TaskCompletionSource<CacheTestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "CrabDesk.IconCacheTest." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var provider = new ShellIconProvider(8);
                var primaryPath = Path.Combine(root, "primary.txt");
                File.WriteAllText(primaryPath, "before");
                var first = provider.GetIcon(primaryPath, 48);
                var second = provider.GetIcon(primaryPath, 48);

                File.AppendAllText(primaryPath, "-after");
                File.SetLastWriteTimeUtc(primaryPath, DateTime.UtcNow.AddSeconds(2));
                var changed = provider.GetIcon(primaryPath, 48);

                for (var index = 0; index < 24; index++)
                {
                    var path = Path.Combine(root, $"item-{index:00}.txt");
                    File.WriteAllText(path, index.ToString());
                    provider.GetIcon(path, 32 + index % 3 * 8);
                }

                var statistics = provider.GetCacheStatistics();
                var cleared = provider.ClearCache();
                completion.SetResult(new CacheTestResult(
                    first is not null,
                    ReferenceEquals(first, second),
                    changed is not null && !ReferenceEquals(first, changed),
                    statistics,
                    cleared,
                    provider.GetCacheStatistics().Count));
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
        thread.Join(TimeSpan.FromSeconds(2));

        Assert.True(result.Loaded);
        Assert.True(result.CacheHitReturnedSameImage);
        Assert.True(result.FileChangeInvalidatedImage);
        Assert.InRange(result.Statistics.Count, 1, 8);
        Assert.True(result.Statistics.Hits >= 1);
        Assert.True(result.Statistics.Misses >= 26);
        Assert.True(result.Statistics.Evictions >= 1);
        Assert.Equal(result.Statistics.Count, result.ClearedCount);
        Assert.Equal(0, result.RemainingCount);
    }

    private sealed record CacheTestResult(
        bool Loaded,
        bool CacheHitReturnedSameImage,
        bool FileChangeInvalidatedImage,
        ShellImageCacheStatistics Statistics,
        int ClearedCount,
        int RemainingCount);
}
