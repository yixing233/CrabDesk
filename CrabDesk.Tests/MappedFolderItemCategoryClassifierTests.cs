using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class MappedFolderItemCategoryClassifierTests
{
    [Theory]
    [InlineData("C:\\Downloads\\photo.webp", DesktopItemKind.File, MappedFolderItemCategory.Image)]
    [InlineData("C:\\Downloads\\notes.pdf", DesktopItemKind.File, MappedFolderItemCategory.Document)]
    [InlineData("C:\\Downloads\\release.7z", DesktopItemKind.File, MappedFolderItemCategory.Archive)]
    [InlineData("C:\\Downloads\\project", DesktopItemKind.Folder, MappedFolderItemCategory.Folder)]
    [InlineData("C:\\Downloads\\setup.exe", DesktopItemKind.File, MappedFolderItemCategory.Other)]
    public void ClassifiesMappedFolderItemsByKindAndExtension(
        string path,
        DesktopItemKind kind,
        MappedFolderItemCategory expected)
    {
        var item = new DesktopItemRef
        {
            Key = new DesktopItemKey("file", path),
            DisplayName = Path.GetFileName(path),
            ParsingName = path,
            FileSystemPath = path,
            Kind = kind
        };

        Assert.Equal(expected, MappedFolderItemCategoryClassifier.GetCategory(item));
    }

    [Fact]
    public void AllCategoryMatchesEveryMappedItem()
    {
        var item = new DesktopItemRef
        {
            Key = new DesktopItemKey("file", "installer"),
            DisplayName = "installer.exe",
            ParsingName = "C:\\Downloads\\installer.exe",
            Kind = DesktopItemKind.File
        };

        Assert.True(MappedFolderItemCategoryClassifier.Matches(MappedFolderItemCategory.All, item));
    }
}
