using CrabDesk.Core;
using CrabDesk.Runtime;
using Xunit;
using Forms = System.Windows.Forms;

namespace CrabDesk.WinUI.Tests;

public sealed class ExternalDesktopDropTests
{
    private const int ShiftKey = 4;
    private const int ControlKey = 8;
    private const Forms.DragDropEffects CopyOrMove = Forms.DragDropEffects.Copy | Forms.DragDropEffects.Move;

    [Theory]
    // Explorer offers Copy|Move|Link for its files: the desktop moves them unless Ctrl asks for a copy.
    [InlineData(CopyOrMove | Forms.DragDropEffects.Link, 0, false, Forms.DragDropEffects.Move)]
    [InlineData(CopyOrMove | Forms.DragDropEffects.Link, ControlKey, false, Forms.DragDropEffects.Copy)]
    [InlineData(CopyOrMove | Forms.DragDropEffects.Link, ShiftKey, false, Forms.DragDropEffects.Move)]
    [InlineData(CopyOrMove | Forms.DragDropEffects.Link, ShiftKey | ControlKey, false, Forms.DragDropEffects.Move)]
    // A source that only allows one operation gets that one, whatever the keys say.
    [InlineData(Forms.DragDropEffects.Copy, 0, false, Forms.DragDropEffects.Copy)]
    [InlineData(Forms.DragDropEffects.Move, ControlKey, false, Forms.DragDropEffects.Move)]
    [InlineData(Forms.DragDropEffects.Link, 0, false, Forms.DragDropEffects.None)]
    // The Recycle Bin only ever moves.
    [InlineData(CopyOrMove, ControlKey, true, Forms.DragDropEffects.Move)]
    [InlineData(Forms.DragDropEffects.Copy, 0, true, Forms.DragDropEffects.None)]
    public void FilesDraggedInFromAnotherApplicationMoveOntoTheDesktopByDefault(
        Forms.DragDropEffects allowedEffects,
        int keyState,
        bool overRecycleBin,
        Forms.DragDropEffects expected)
    {
        Assert.Equal(
            expected,
            DesktopIconSurface.ResolveExternalFileDropEffect(allowedEffects, keyState, overRecycleBin));
    }

    [Fact]
    public void DropEffectDoesNotDependOnTheSourceVolume()
    {
        // The old rule copied cross-volume drops like Explorer does between folders; on the
        // desktop that made every drag from D: leave a duplicate behind. The resolver takes no
        // paths at all now, so the same allowed mask always yields the same answer.
        var effect = DesktopIconSurface.ResolveExternalFileDropEffect(CopyOrMove, 0, overRecycleBin: false);
        Assert.Equal(Forms.DragDropEffects.Move, effect);
    }

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
