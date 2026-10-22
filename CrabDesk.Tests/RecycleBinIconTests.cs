using CrabDesk.Native;

namespace CrabDesk.Tests;

public sealed class RecycleBinIconTests
{
    private const string RecycleBin = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    [Fact]
    public void NativeQueryInitializesTheRecycleBinIconState()
    {
        var provider = new ShellIconProvider();

        Assert.True(provider.RefreshRecycleBinState());
        Assert.NotEqual(0, provider.GetIconCacheStamp(RecycleBin));
    }

    [Fact]
    public void EmptyingAndRefillingTheRecycleBinChangesItsImageCacheStamp()
    {
        var provider = new ShellIconProvider();
        Assert.True(provider.UpdateRecycleBinState(false));
        var fullStamp = provider.GetIconCacheStamp(RecycleBin);

        Assert.True(provider.UpdateRecycleBinState(true));
        var emptyStamp = provider.GetIconCacheStamp(RecycleBin);
        Assert.NotEqual(fullStamp, emptyStamp);

        Assert.True(provider.UpdateRecycleBinState(false));
        Assert.NotEqual(fullStamp, provider.GetIconCacheStamp(RecycleBin));
        Assert.NotEqual(emptyStamp, provider.GetIconCacheStamp(RecycleBin));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(null)]
    public void UnchangedOrUnavailableStateDoesNotInvalidateImages(bool? nextState)
    {
        var provider = new ShellIconProvider();
        provider.UpdateRecycleBinState(nextState ?? false);
        var stamp = provider.GetIconCacheStamp(RecycleBin);

        Assert.False(provider.UpdateRecycleBinState(nextState));
        Assert.Equal(stamp, provider.GetIconCacheStamp(RecycleBin));
    }

    [Theory]
    [InlineData(@"C:\Desktop\notes.txt")]
    [InlineData("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")]
    public void RecycleBinChangesPreserveOtherItemsCacheStamps(string parsingName)
    {
        var provider = new ShellIconProvider();
        provider.UpdateRecycleBinState(false);
        var stamp = provider.GetIconCacheStamp(parsingName, 12345);
        provider.UpdateRecycleBinState(true);

        Assert.Equal(12345, stamp);
        Assert.Equal(stamp, provider.GetIconCacheStamp(parsingName, 12345));
    }

    [Fact]
    public void EmptyingTheRecycleBinLoadsANewImageInsteadOfReturningTheCachedFullImage()
    {
        var provider = new ShellIconProvider();
        try
        {
            provider.UpdateRecycleBinState(false);
            var full = provider.GetIcon(RecycleBin, 48);
            Assert.NotNull(full);
            Assert.Same(full, provider.GetIcon(RecycleBin, 48));

            provider.UpdateRecycleBinState(true);
            var empty = provider.GetIcon(RecycleBin, 48);
            Assert.NotNull(empty);
            Assert.NotSame(full, empty);
            Assert.Same(empty, provider.GetIcon(RecycleBin, 48));
            Assert.Equal(2, provider.GetCacheStatistics().Misses);
        }
        finally
        {
            provider.ClearCache();
        }
    }

    [Theory]
    [InlineData(RecycleBin)]
    [InlineData("shell:::{645FF040-5081-101B-9F08-00AA002F954E}")]
    public void FullAndEmptyRecycleBinImagesHaveDifferentPixels(string parsingName)
    {
        var provider = new ShellIconProvider();
        try
        {
            provider.UpdateRecycleBinState(false);
            var full = provider.GetIcon(parsingName, 48);
            provider.UpdateRecycleBinState(true);
            var empty = provider.GetIcon(parsingName, 48);
            Assert.NotNull(full);
            Assert.NotNull(empty);

            var fullPixels = Enumerable.Range(0, full!.Width)
                .SelectMany(x => Enumerable.Range(0, full.Height).Select(y => full.GetPixel(x, y).ToArgb()));
            var emptyPixels = Enumerable.Range(0, empty!.Width)
                .SelectMany(x => Enumerable.Range(0, empty.Height).Select(y => empty.GetPixel(x, y).ToArgb()));
            Assert.False(fullPixels.SequenceEqual(emptyPixels),
                "The empty and full states must produce visibly different images, not just new bitmap instances.");
        }
        finally
        {
            provider.ClearCache();
        }
    }
}
