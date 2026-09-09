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

    [Fact]
    public async Task DeleteWithNoFileSystemItemsCompletesWithoutError()
    {
        var shellItem = new DesktopItemRef
        {
            Key = new DesktopItemKey("shell", "ThisPC"),
            DisplayName = "此电脑",
            ParsingName = "::{ThisPC}",
            FileSystemPath = null,
            Kind = DesktopItemKind.Shell
        };

        await new FileOperationService().DeleteAsync([shellItem]);
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ReadOnlyMappedSourceAlwaysCopies(bool shiftPressed, bool controlPressed)
    {
        Assert.Equal(
            BoxTransferEffect.CopyFiles,
            BoxTransferPolicy.Resolve(
                internalItems: true,
                sourceMapped: true,
                targetMapped: false,
                shiftPressed: shiftPressed,
                controlPressed: controlPressed,
                sourceMappedReadOnly: true));
    }

    [Theory]
    [InlineData(true, false, false, false, false, false, true)]
    [InlineData(false, false, false, false, false, false, false)]
    [InlineData(true, true, false, false, false, false, false)]
    [InlineData(true, false, true, false, false, false, false)]
    [InlineData(true, false, false, true, false, false, false)]
    [InlineData(true, false, false, false, true, false, false)]
    [InlineData(true, false, false, false, false, true, false)]
    public void DragCompletionOnlyUnassignsCommittedDropOutsideEveryBox(
        bool committed,
        bool cancelled,
        bool handledByBox,
        bool sourceMapped,
        bool pointerOverBox,
        bool externalDropAccepted,
        bool expected)
    {
        Assert.Equal(expected, BoxDragCompletionPolicy.ShouldUnassign(
            committed,
            cancelled,
            handledByBox,
            sourceMapped,
            pointerOverBox,
            externalDropAccepted));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void FileDropRequiresFileSystemPathForEverySelectedItem(
        bool allSelectedItemsHavePaths,
        bool expected)
    {
        Assert.Equal(expected, BoxDragCompletionPolicy.ShouldExposeFileDrop(allSelectedItemsHavePaths));
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
    public async Task RenameTreatsDotPrefixedFilenameAsItsOwnStem()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, ".gitignore");
        await File.WriteAllTextAsync(source, "bin/");
        var item = new DesktopItemRef
        {
            Key = new DesktopItemKey("path", source),
            DisplayName = ".gitignore",
            ParsingName = source,
            FileSystemPath = source,
            Kind = DesktopItemKind.File
        };

        var destination = await new FileOperationService().RenameAsync(item, ".dockerignore");

        Assert.Equal(Path.Combine(_root, ".dockerignore"), destination);
        Assert.True(File.Exists(destination));
        Assert.False(File.Exists(source));
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

        Assert.Equal(2, imported.SucceededCount);
        Assert.Equal(0, imported.FailedCount);
        Assert.False(File.Exists(sourceFile));
        Assert.False(Directory.Exists(sourceFolder));
        Assert.Equal("document", await File.ReadAllTextAsync(Path.Combine(destination, "document.txt")));
        Assert.Equal("nested", await File.ReadAllTextAsync(Path.Combine(destination, "folder", "nested.txt")));
    }

    [Fact]
    public async Task ImportRenamesCollidingNamesWithAnUnderscoreNumberSuffix()
    {
        var sources = Path.Combine(_root, "sources");
        var destination = Path.Combine(_root, "destination");
        var sourceFile = Path.Combine(sources, "Snipaste_2025-12-03.png");
        var sourceFolder = Path.Combine(sources, "folder");
        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(sourceFile, "image");
        await File.WriteAllTextAsync(Path.Combine(sourceFolder, "nested.txt"), "nested");
        var service = new FileOperationService();

        var first = await service.ImportAsync([sourceFile, sourceFolder], destination, false);
        var second = await service.ImportAsync([sourceFile, sourceFolder], destination, false);
        var third = await service.ImportAsync([sourceFile], destination, false);

        Assert.Equal(2, first.SucceededCount);
        Assert.Equal(2, second.SucceededCount);
        Assert.Equal(1, third.SucceededCount);
        Assert.Equal(
            Path.Combine(destination, "Snipaste_2025-12-03.png"),
            first.SuccessfulItems[0].DestinationPath);
        Assert.Equal(
            Path.Combine(destination, "Snipaste_2025-12-03_2.png"),
            second.SuccessfulItems[0].DestinationPath);
        Assert.Equal(
            Path.Combine(destination, "folder_2"),
            second.SuccessfulItems[1].DestinationPath);
        Assert.Equal(
            Path.Combine(destination, "Snipaste_2025-12-03_3.png"),
            third.SuccessfulItems[0].DestinationPath);
        Assert.Equal(
            "nested",
            await File.ReadAllTextAsync(Path.Combine(destination, "folder_2", "nested.txt")));
    }

    [Fact]
    public async Task ImportContinuesAfterAnIndividualPathFails()
    {
        var sources = Path.Combine(_root, "sources");
        var destination = Path.Combine(_root, "destination");
        var first = Path.Combine(sources, "first.txt");
        var missing = Path.Combine(sources, "missing.txt");
        var second = Path.Combine(sources, "second.txt");
        Directory.CreateDirectory(sources);
        await File.WriteAllTextAsync(first, "first");
        await File.WriteAllTextAsync(second, "second");

        var imported = await new FileOperationService().ImportAsync(
            [first, missing, second],
            destination,
            false);

        Assert.Equal(2, imported.SucceededCount);
        Assert.Equal(1, imported.FailedCount);
        Assert.Equal(missing, imported.FailedItems.Single().SourcePath);
        Assert.True(File.Exists(Path.Combine(destination, "first.txt")));
        Assert.True(File.Exists(Path.Combine(destination, "second.txt")));
    }

    [Fact]
    public async Task CrossVolumeMovePreservesDestinationWhenSourceRemovalFails()
    {
        if (!TryGetDistinctVolumes(out var sourceVolume, out var destinationVolume))
        {
            return;
        }

        var sourceDir = Path.Combine(sourceVolume, "CrabDesk.Tests", Guid.NewGuid().ToString("N"));
        var destinationDir = Path.Combine(destinationVolume, "CrabDesk.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(destinationDir);

            var sourceFile = Path.Combine(sourceDir, "held.txt");
            await File.WriteAllTextAsync(sourceFile, "important content");

            using (var stream = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var result = await new FileOperationService().ImportAsync([sourceFile], destinationDir, move: true);

                Assert.Equal(0, result.SucceededCount);
                Assert.Equal(1, result.FailedCount);

                var expectedDestination = Path.Combine(destinationDir, Path.GetFileName(sourceFile));
                Assert.True(File.Exists(expectedDestination));
                Assert.Equal("important content", await File.ReadAllTextAsync(expectedDestination));
                Assert.Contains("目标副本已保留", result.FailedItems.Single().ErrorMessage);
            }
        }
        finally
        {
            if (Directory.Exists(sourceDir))
            {
                Directory.Delete(sourceDir, true);
            }
            if (Directory.Exists(destinationDir))
            {
                Directory.Delete(destinationDir, true);
            }
        }
    }

    [Fact]
    public async Task CrossVolumeDirectoryMovePreservesDestinationWhenSourceRemovalFails()
    {
        if (!TryGetDistinctVolumes(out var sourceVolume, out var destinationVolume))
        {
            return;
        }

        var sourceDir = Path.Combine(sourceVolume, "CrabDesk.Tests", Guid.NewGuid().ToString("N"));
        var destinationRoot = Path.Combine(destinationVolume, "CrabDesk.Tests", Guid.NewGuid().ToString("N"));
        var lockedPath = Path.Combine(sourceDir, "z.txt");
        try
        {
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(destinationRoot);

            await File.WriteAllTextAsync(Path.Combine(sourceDir, "a.txt"), "first");
            await File.WriteAllTextAsync(lockedPath, "locked");

            using (var held = new FileStream(
                lockedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                var service = new FileOperationService();
                var result = await service.ImportAsync([sourceDir], destinationRoot, true);

                Assert.Equal(1, result.FailedCount);
                Assert.Equal(0, result.SucceededCount);
                var expectedDestDir = Path.Combine(destinationRoot, Path.GetFileName(sourceDir));
                Assert.True(Directory.Exists(expectedDestDir));
                Assert.Equal("first", await File.ReadAllTextAsync(
                    Path.Combine(expectedDestDir, "a.txt")));
                Assert.Equal("locked", await File.ReadAllTextAsync(
                    Path.Combine(expectedDestDir, "z.txt")));
                Assert.True(File.Exists(lockedPath));
                Assert.Contains("目标副本已保留", result.FailedItems.Single().ErrorMessage);
            }
        }
        finally
        {
            if (Directory.Exists(sourceDir))
            {
                Directory.Delete(sourceDir, true);
            }
            if (Directory.Exists(destinationRoot))
            {
                Directory.Delete(destinationRoot, true);
            }
        }
    }

    [Fact]
    public async Task CrossVolumeMoveSucceedsNormally()
    {
        if (!TryGetDistinctVolumes(out var sourceVolume, out var destinationVolume))
        {
            return;
        }

        var sourceDir = Path.Combine(sourceVolume, "CrabDesk.Tests", Guid.NewGuid().ToString("N"));
        var destinationRoot = Path.Combine(destinationVolume, "CrabDesk.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(destinationRoot);

            var fileInDir = Path.Combine(sourceDir, "item.txt");
            await File.WriteAllTextAsync(fileInDir, "data");

            var service = new FileOperationService();
            var result = await service.ImportAsync([sourceDir], destinationRoot, move: true);

            Assert.Equal(1, result.SucceededCount);
            Assert.Equal(0, result.FailedCount);
            Assert.False(Directory.Exists(sourceDir));
            var expectedDest = Path.Combine(destinationRoot, Path.GetFileName(sourceDir));
            Assert.True(Directory.Exists(expectedDest));
            Assert.Equal("data", await File.ReadAllTextAsync(Path.Combine(expectedDest, "item.txt")));
        }
        finally
        {
            if (Directory.Exists(sourceDir))
            {
                Directory.Delete(sourceDir, true);
            }
            if (Directory.Exists(destinationRoot))
            {
                Directory.Delete(destinationRoot, true);
            }
        }
    }

    [Fact]
    public async Task CrossVolumeBatchImportContinuesAfterSourceRemovalFailure()
    {
        if (!TryGetDistinctVolumes(out var sourceVolume, out var destinationVolume))
        {
            return;
        }

        var sourceRoot = Path.Combine(sourceVolume, "CrabDesk.Tests", Guid.NewGuid().ToString("N"));
        var destinationRoot = Path.Combine(destinationVolume, "CrabDesk.Tests", Guid.NewGuid().ToString("N"));
        var failedFile = Path.Combine(sourceRoot, "failed.txt");
        var succeedFile = Path.Combine(sourceRoot, "succeed.txt");
        try
        {
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(destinationRoot);

            await File.WriteAllTextAsync(failedFile, "failed content");
            await File.WriteAllTextAsync(succeedFile, "succeed content");

            using (var stream = new FileStream(failedFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var result = await new FileOperationService().ImportAsync(
                    [failedFile, succeedFile],
                    destinationRoot,
                    move: true);

                Assert.Equal(1, result.SucceededCount);
                Assert.Equal(1, result.FailedCount);
                Assert.Equal(failedFile, result.FailedItems.Single().SourcePath);
                Assert.Contains("目标副本已保留", result.FailedItems.Single().ErrorMessage);

                var failedDest = Path.Combine(destinationRoot, "failed.txt");
                var succeedDest = Path.Combine(destinationRoot, "succeed.txt");
                Assert.True(File.Exists(failedDest));
                Assert.Equal("failed content", await File.ReadAllTextAsync(failedDest));
                Assert.True(File.Exists(succeedDest));
                Assert.Equal("succeed content", await File.ReadAllTextAsync(succeedDest));
                Assert.False(File.Exists(succeedFile));
            }
        }
        finally
        {
            if (Directory.Exists(sourceRoot))
            {
                Directory.Delete(sourceRoot, true);
            }
            if (Directory.Exists(destinationRoot))
            {
                Directory.Delete(destinationRoot, true);
            }
        }
    }

    private static bool TryGetDistinctVolumes(out string sourceVolume, out string destinationVolume)
    {
        var candidates = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
            {
                continue;
            }

            try
            {
                var probeDir = Path.Combine(drive.RootDirectory.FullName, "CrabDesk.Tests", "probe_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(probeDir);
                Directory.Delete(probeDir, true);
                candidates.Add(drive.RootDirectory.FullName);
            }
            catch
            {
                // Volume not writable
            }
        }

        if (candidates.Count >= 2)
        {
            sourceVolume = candidates[0];
            destinationVolume = candidates[1];
            return true;
        }

        sourceVolume = string.Empty;
        destinationVolume = string.Empty;
        return false;
    }

    [Fact]
    public async Task ImportRejectsCopyingDirectoryIntoItself()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "CrabDesk.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(testDir);
            var fileInDir = Path.Combine(testDir, "data.txt");
            await File.WriteAllTextAsync(fileInDir, "hello");

            var service = new FileOperationService();
            var result = await service.ImportAsync([testDir], testDir, move: false);

            Assert.Equal(0, result.SucceededCount);
            Assert.Equal(1, result.FailedCount);
            Assert.Contains("不能将文件夹复制或移动到其自身或子文件夹中", result.FailedItems.Single().ErrorMessage);
            Assert.True(Directory.Exists(testDir));
            Assert.True(File.Exists(fileInDir));
            Assert.Equal("hello", await File.ReadAllTextAsync(fileInDir));
            Assert.Empty(Directory.GetDirectories(testDir));
        }
        finally
        {
            if (Directory.Exists(testDir))
            {
                Directory.Delete(testDir, true);
            }
        }
    }

    [Fact]
    public async Task ImportRejectsMovingDirectoryIntoItsOwnDescendant()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "CrabDesk.Tests", Guid.NewGuid().ToString("N"));
        var subDir = Path.Combine(rootDir, "SubFolder");
        try
        {
            Directory.CreateDirectory(subDir);
            var rootFile = Path.Combine(rootDir, "root.txt");
            var subFile = Path.Combine(subDir, "sub.txt");
            await File.WriteAllTextAsync(rootFile, "root content");
            await File.WriteAllTextAsync(subFile, "sub content");

            var service = new FileOperationService();
            var result = await service.ImportAsync([rootDir], subDir, move: true);

            Assert.Equal(0, result.SucceededCount);
            Assert.Equal(1, result.FailedCount);
            Assert.Contains("不能将文件夹复制或移动到其自身或子文件夹中", result.FailedItems.Single().ErrorMessage);
            Assert.True(Directory.Exists(rootDir));
            Assert.True(Directory.Exists(subDir));
            Assert.True(File.Exists(rootFile));
            Assert.True(File.Exists(subFile));
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, true);
            }
        }
    }

    [Fact]
    public async Task ImportAllowsCopyingDirectoryToPrefixSibling()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "CrabDesk.Tests", Guid.NewGuid().ToString("N"));
        var sourceDir = Path.Combine(baseDir, "Folder");
        var destinationDir = Path.Combine(baseDir, "Folder-Copy");
        try
        {
            Directory.CreateDirectory(sourceDir);
            var file = Path.Combine(sourceDir, "file.txt");
            await File.WriteAllTextAsync(file, "prefix test");

            var service = new FileOperationService();
            var result = await service.ImportAsync([sourceDir], destinationDir, move: false);

            Assert.Equal(1, result.SucceededCount);
            Assert.Equal(0, result.FailedCount);
            var copiedFile = Path.Combine(destinationDir, "Folder", "file.txt");
            Assert.True(File.Exists(copiedFile));
            Assert.Equal("prefix test", await File.ReadAllTextAsync(copiedFile));
        }
        finally
        {
            if (Directory.Exists(baseDir))
            {
                Directory.Delete(baseDir, true);
            }
        }
    }

    [Fact]
    public async Task ImportCancellationThrowsAndCleansUpIncompleteDestination()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "CrabDesk.Tests", Guid.NewGuid().ToString("N"));
        var sourceDir = Path.Combine(baseDir, "Source");
        var destinationDir = Path.Combine(baseDir, "Destination");
        try
        {
            Directory.CreateDirectory(sourceDir);
            for (int i = 0; i < 20; i++)
            {
                await File.WriteAllTextAsync(Path.Combine(sourceDir, $"file_{i}.txt"), new string('x', 10000));
            }

            var service = new FileOperationService();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await service.ImportAsync([sourceDir], destinationDir, move: false, cts.Token);
            });

            var targetFolder = Path.Combine(destinationDir, "Source");
            Assert.False(Directory.Exists(targetFolder));
        }
        finally
        {
            if (Directory.Exists(baseDir))
            {
                Directory.Delete(baseDir, true);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportHonorsRequestedMoveEffect(bool move)
    {
        var sourceDirectory = Path.Combine(_root, "effect-source");
        var destination = Path.Combine(_root, "effect-target");
        Directory.CreateDirectory(sourceDirectory);
        var source = Path.Combine(sourceDirectory, "item.txt");
        await File.WriteAllTextAsync(source, "content");

        var result = await new FileOperationService().ImportAsync([source], destination, move);

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(!move, File.Exists(source));
        Assert.Equal("content", await File.ReadAllTextAsync(Path.Combine(destination, "item.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportRejectsSourceAndDescendantDestinations(bool descendant)
    {
        var source = Path.Combine(_root, "parent");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "original.txt"), "original");
        var destination = descendant ? Path.Combine(source, "child") : source;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var result = await new FileOperationService().ImportAsync(
            [source], destination, false, timeout.Token);

        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, result.SucceededCount);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(source, "original.txt")));
        Assert.Empty(Directory.GetDirectories(source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportRejectsMovingSourceToSourceOrDescendant(bool descendant)
    {
        var source = Path.Combine(_root, "move-parent");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "data.txt"), "data");
        var destination = descendant ? Path.Combine(source, "move-child") : source;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var result = await new FileOperationService().ImportAsync(
            [source], destination, move: true, timeout.Token);

        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, result.SucceededCount);
        Assert.Contains("不能将文件夹复制或移动到其自身或子文件夹中", result.FailedItems.Single().ErrorMessage);
        Assert.Equal("data", await File.ReadAllTextAsync(Path.Combine(source, "data.txt")));
    }

    [Theory]
    [InlineData("cased")]
    [InlineData("trailing-slash")]
    [InlineData("dot-dot")]
    public async Task ImportRejectsEquivalentPathsToSelfOrDescendant(string variant)
    {
        var parent = Path.Combine(_root, "equivalent-parent");
        Directory.CreateDirectory(parent);
        var sub = Path.Combine(parent, "sub");
        Directory.CreateDirectory(sub);
        await File.WriteAllTextAsync(Path.Combine(parent, "info.txt"), "info");

        string destination = variant switch
        {
            "cased" => parent.ToUpperInvariant(),
            "trailing-slash" => parent + Path.DirectorySeparatorChar,
            "dot-dot" => Path.Combine(sub, ".."),
            _ => throw new ArgumentException()
        };

        var result = await new FileOperationService().ImportAsync([parent], destination, move: false);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, result.SucceededCount);
        Assert.Contains("不能将文件夹复制或移动到其自身或子文件夹中", result.FailedItems.Single().ErrorMessage);
    }

    [Fact]
    public async Task ImportDirectoryToParentDirectoryCreatesCollidedCopy()
    {
        var folder = Path.Combine(_root, "TargetFolder");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "sub.txt"), "sub content");

        var result = await new FileOperationService().ImportAsync([folder], _root, move: false);
        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);

        var copyFolder = Path.Combine(_root, "TargetFolder_2");
        Assert.True(Directory.Exists(copyFolder));
        Assert.True(File.Exists(Path.Combine(copyFolder, "sub.txt")));
    }

    [Fact]
    public async Task MixedBatchImportFailsInvalidDirectoryWhileSucceedingValidFile()
    {
        var sourceDir = Path.Combine(_root, "invalid-dir");
        Directory.CreateDirectory(sourceDir);
        var validFile = Path.Combine(_root, "valid-file.txt");
        await File.WriteAllTextAsync(validFile, "valid");

        var destination = sourceDir;

        var result = await new FileOperationService().ImportAsync(
            [sourceDir, validFile],
            destination,
            move: false);

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(sourceDir, result.FailedItems.Single().SourcePath);
        Assert.Contains("不能将文件夹复制或移动到其自身或子文件夹中", result.FailedItems.Single().ErrorMessage);
        Assert.True(File.Exists(Path.Combine(destination, "valid-file.txt")));
    }

    [Fact]
    public async Task ImportMidCopyCancellationCleansUpIncompleteDestination()
    {
        var sourceDir = Path.Combine(_root, "large-source");
        Directory.CreateDirectory(sourceDir);
        for (int i = 0; i < 50; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(sourceDir, $"file_{i}.dat"), new string('a', 50000));
        }

        var destinationDir = Path.Combine(_root, "large-dest");
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(10));

        var service = new FileOperationService();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await service.ImportAsync([sourceDir], destinationDir, move: false, cts.Token);
        });

        var incompleteDest = Path.Combine(destinationDir, "large-source");
        Assert.False(Directory.Exists(incompleteDest));
        Assert.Equal(50, Directory.GetFiles(sourceDir).Length);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
