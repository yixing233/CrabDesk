using CrabDesk.Core;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class ExternalDesktopDropTests
{
    [Fact]
    public void ImportedItemKeysFollowTheFileDropPathOrder()
    {
        var firstPath = Path.GetFullPath(Path.Combine("external-drop", "first.txt"));
        var secondPath = Path.GetFullPath(Path.Combine("external-drop", "second.txt"));
        var runtimeItems = new[]
        {
            Item("file:SECOND", secondPath),
            Item("file:FIRST", firstPath)
        };

        var keys = DesktopIconSurface.ResolveImportedItemKeysInDropOrder(
            [firstPath, secondPath],
            runtimeItems);

        Assert.Equal(["file:FIRST", "file:SECOND"], keys);
    }

    [Fact]
    public void ExplorerDesktopItemIsRecognizedAsAnExistingDesktopPath()
    {
        var desktopPath = Path.GetFullPath(Path.Combine("desktop", "existing.txt"));
        var runtimeItems = new[] { Item("file:EXISTING", desktopPath) };

        Assert.True(DesktopIconSurface.IsExistingDesktopItemPath(
            desktopPath,
            runtimeItems));
        Assert.False(DesktopIconSurface.IsExistingDesktopItemPath(
            Path.Combine(Path.GetDirectoryName(desktopPath)!, "other.txt"),
            runtimeItems));
    }

    private static DesktopItemRef Item(string key, string path) => new()
    {
        Key = DesktopItemKey.Parse(key),
        DisplayName = Path.GetFileName(path),
        ParsingName = path,
        FileSystemPath = path,
        Kind = DesktopItemKind.File
    };
}
