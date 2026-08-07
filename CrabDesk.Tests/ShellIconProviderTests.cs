using CrabDesk.Native;

namespace CrabDesk.Tests;

public sealed class ShellIconProviderTests
{
    [Fact]
    public void FailedShellLookupsAreNotCachedAsPermanentBlankIcons()
    {
        var missingPath = Path.Combine(
            Path.GetTempPath(),
            $"CrabDeskMissing-{Guid.NewGuid():N}",
            "missing.no-such-extension");
        var provider = new ShellIconProvider();

        Assert.Null(provider.GetIcon(missingPath, 48));
        Assert.Null(provider.GetIcon(missingPath, 48));

        var statistics = provider.GetCacheStatistics();
        Assert.Equal(0, statistics.Count);
        Assert.Equal(2, statistics.Misses);
    }

    [Fact]
    public void ShellIconsRetainTransparentPixels()
    {
        var path = Path.Combine(Path.GetTempPath(), $"CrabDeskIcon-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "icon alpha test");
        var provider = new ShellIconProvider();
        try
        {
            var bitmap = provider.GetIcon(path, 48);

            Assert.NotNull(bitmap);
            var alphaValues = Enumerable.Range(0, bitmap!.Width)
                .SelectMany(x => Enumerable.Range(0, bitmap.Height)
                    .Select(y => bitmap.GetPixel(x, y).A));
            Assert.Contains(alphaValues, alpha => alpha < byte.MaxValue);
        }
        finally
        {
            provider.ClearCache();
            File.Delete(path);
        }
    }
}
