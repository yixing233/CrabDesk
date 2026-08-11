using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class DesktopItemNameTests
{
    [Theory]
    [InlineData(".minecraft", true, ".minecraft")]
    [InlineData(".gitignore", false, ".gitignore")]
    [InlineData(".env.local", false, ".env")]
    [InlineData("notes.txt", false, "notes")]
    public void GetsDisplayNamesWithoutDroppingDotPrefixedEntries(
        string fileName,
        bool isDirectory,
        string expected)
    {
        Assert.Equal(expected, DesktopItemName.GetDisplayName(fileName, isDirectory));
    }

    [Fact]
    public void DotPrefixedFilenameHasNoSyntheticExtension()
    {
        Assert.Equal((".gitignore", string.Empty), DesktopItemName.SplitFileName(".gitignore"));
    }
}
