using CrabDesk.Core;
using CrabDesk.Native;

namespace CrabDesk.Tests;

public sealed class FileDeleteBatchTests : IDisposable
{
    // A locked file is the real-world case: a document still open in its editor
    // must not stop the rest of the selection from reaching the Recycle Bin.
    // Uses a plain file handle with FileShare.None to force the shell failure.
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "CrabDesk.FileDeleteBatchTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AFailedItemDoesNotStopTheItemsAfterIt()
    {
        Directory.CreateDirectory(_root);
        var lockedPath = Path.Combine(_root, "a-locked.txt");
        var laterPath = Path.Combine(_root, "b-after.txt");
        await File.WriteAllTextAsync(lockedPath, "locked");
        await File.WriteAllTextAsync(laterPath, "later");

        using (File.Open(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await new FileOperationService().DeleteAsync(
                [Item(lockedPath), Item(laterPath)]);

            // Both entries report an outcome: the batch never abandoned the
            // second path because the first one failed.
            Assert.Equal(2, result.Items.Count);
            var failure = Assert.Single(result.FailedItems);
            Assert.Equal(lockedPath, failure.Path);
            Assert.False(string.IsNullOrWhiteSpace(failure.ErrorMessage));

            // The item after the locked one was still attempted and succeeded.
            var succeeded = Assert.Single(result.SucceededItems);
            Assert.Equal(laterPath, succeeded.Path);
            Assert.Equal([laterPath], result.SucceededPaths);
        }
    }

    [Fact]
    public async Task AFailedItemIsNotReportedAsARemovedPath()
    {
        Directory.CreateDirectory(_root);
        var lockedPath = Path.Combine(_root, "locked-only.txt");
        await File.WriteAllTextAsync(lockedPath, "locked");

        using (File.Open(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await new FileOperationService().DeleteAsync([Item(lockedPath)]);

            Assert.True(result.HasFailures);
            // The file is still on the desktop, so reconciling its path as
            // removed would blank a cell that still holds a real file.
            Assert.Empty(result.SucceededPaths);
            Assert.Empty(result.SucceededItems);
        }
    }

    [Fact]
    public async Task DuplicatePathsFromMultipleSurfacesAreDeletedOnce()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "duplicate.txt");
        await File.WriteAllTextAsync(path, "duplicate");

        var result = await new FileOperationService().DeleteAsync(
            [Item(path), Item(path.ToUpperInvariant())]);

        Assert.Single(result.Items);
        Assert.False(result.HasFailures);
    }

    [Fact]
    public async Task ItemsWithoutAFileSystemPathAreIgnored()
    {
        var shellItem = new DesktopItemRef
        {
            Key = new DesktopItemKey("shell", "ThisPC"),
            DisplayName = "此电脑",
            ParsingName = "::{ThisPC}",
            FileSystemPath = null,
            Kind = DesktopItemKind.Shell
        };

        var result = await new FileOperationService().DeleteAsync([shellItem]);

        Assert.Empty(result.Items);
        Assert.False(result.HasFailures);
        Assert.Equal(0, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);
    }

    private static DesktopItemRef Item(string path) => new()
    {
        Key = new DesktopItemKey("file", path.ToUpperInvariant()),
        DisplayName = Path.GetFileName(path),
        ParsingName = path,
        FileSystemPath = path,
        Kind = DesktopItemKind.File
    };

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
