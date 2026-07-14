using CrabDesk.Core;
using CrabDesk.Native;

namespace CrabDesk.Tests;

public sealed class FileOperationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "CrabDesk.FileOperationTests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FileClipboardCodecRoundTripsPathsAndPreferredEffect(bool move)
    {
        var first = Path.Combine(_root, "first.txt");
        var second = Path.Combine(_root, "folder");

        var data = FileClipboardCodec.Create([first, second, first], move);
        var decoded = FileClipboardCodec.Read(data);

        Assert.Equal(move, decoded.Move);
        Assert.Equal([Path.GetFullPath(first), Path.GetFullPath(second)], decoded.Paths);
    }

    [Theory]
    [InlineData(true, false, false, false, false, BoxTransferEffect.VirtualMove)]
    [InlineData(true, false, false, true, false, BoxTransferEffect.VirtualMove)]
    [InlineData(false, false, false, false, false, BoxTransferEffect.CopyFiles)]
    [InlineData(false, false, false, true, false, BoxTransferEffect.MoveFiles)]
    [InlineData(false, false, true, true, true, BoxTransferEffect.CopyFiles)]
    [InlineData(true, true, false, false, false, BoxTransferEffect.CopyFiles)]
    [InlineData(true, true, false, true, false, BoxTransferEffect.MoveFiles)]
    [InlineData(true, false, true, true, false, BoxTransferEffect.MoveFiles)]
    public void BoxTransferPolicyResolvesVirtualCopyAndMoveSemantics(
        bool internalItems,
        bool sourceMapped,
        bool targetMapped,
        bool shift,
        bool control,
        BoxTransferEffect expected)
    {
        Assert.Equal(expected, BoxTransferPolicy.Resolve(
            internalItems,
            sourceMapped,
            targetMapped,
            shift,
            control));
    }

    [Fact]
    public async Task RenamePreservesExistingExtensionWithoutDuplicatingIt()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "before.txt");
        await File.WriteAllTextAsync(source, "rename");
        var item = new DesktopItemRef
        {
            Key = new DesktopItemKey("path", source),
            DisplayName = "before",
            ParsingName = source,
            FileSystemPath = source,
            Kind = DesktopItemKind.File
        };

        var destination = await new FileOperationService().RenameAsync(item, "after.txt");

        Assert.Equal(Path.Combine(_root, "after.txt"), destination);
        Assert.True(File.Exists(destination));
        Assert.False(File.Exists(source));
        Assert.False(File.Exists(destination + ".txt"));
    }

    [Fact]
    public async Task RenameRejectsInvalidOrExistingDestination()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.txt");
        var existing = Path.Combine(_root, "existing.txt");
        await File.WriteAllTextAsync(source, "source");
        await File.WriteAllTextAsync(existing, "existing");
        var item = new DesktopItemRef
        {
            Key = new DesktopItemKey("path", source),
            DisplayName = "source",
            ParsingName = source,
            FileSystemPath = source,
            Kind = DesktopItemKind.File
        };
        var service = new FileOperationService();

        await Assert.ThrowsAsync<ArgumentException>(() => service.RenameAsync(item, "bad/name"));
        await Assert.ThrowsAsync<IOException>(() => service.RenameAsync(item, "existing"));
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(existing));
    }

    [Fact]
    public async Task MoveImportMovesFilesAndDirectoriesWithoutLeavingSources()
    {
        var sources = Path.Combine(_root, "sources");
        var destination = Path.Combine(_root, "destination");
        var sourceFile = Path.Combine(sources, "document.txt");
        var sourceFolder = Path.Combine(sources, "folder");
        Directory.CreateDirectory(sourceFolder);
        await File.WriteAllTextAsync(sourceFile, "document");
        await File.WriteAllTextAsync(Path.Combine(sourceFolder, "nested.txt"), "nested");

        var imported = await new FileOperationService().ImportAsync(
            [sourceFile, sourceFolder],
            destination,
            true);

        Assert.Equal(2, imported.Count);
        Assert.False(File.Exists(sourceFile));
        Assert.False(Directory.Exists(sourceFolder));
        Assert.Equal("document", await File.ReadAllTextAsync(Path.Combine(destination, "document.txt")));
        Assert.Equal("nested", await File.ReadAllTextAsync(Path.Combine(destination, "folder", "nested.txt")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
