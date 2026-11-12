using System.Diagnostics;
using System.Text.Json;
using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class WidgetFileShelfTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CrabDesk.FileShelfTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReferenceRemovalPreservesOriginalFile()
    {
        var source = await CreateSourceAsync("source/report.txt", "original");
        var widgetId = Guid.NewGuid();
        var store = new WidgetFileShelfStore(_root);

        var item = await store.AddAsync(widgetId, source, WidgetFileShelfMode.Reference);
        store.Remove(widgetId, item);

        Assert.Equal(source, store.ResolvePath(widgetId, item));
        Assert.Equal("report.txt", item.Name);
        Assert.Equal(WidgetFileShelfMode.Reference, item.Mode);
        Assert.Empty(item.StoredRelativePath);
        Assert.True(item.IsAvailable);
        Assert.NotEqual(default, item.AddedAt);
        Assert.Equal("original", await File.ReadAllTextAsync(source));
        Assert.False(Directory.Exists(Path.Combine(_root, "FileShelf")));
    }

    [Fact]
    public async Task ManagedCopySurvivesSourceEditsAndDeletion()
    {
        var source = await CreateSourceAsync("source/report.txt", "original");
        var widgetId = Guid.NewGuid();
        var store = new WidgetFileShelfStore(_root);

        var item = await store.AddAsync(widgetId, source, WidgetFileShelfMode.Copy);
        var copy = store.ResolvePath(widgetId, item);
        await File.WriteAllTextAsync(source, "edited");
        Assert.Equal("original", await File.ReadAllTextAsync(copy));
        File.Delete(source);

        Assert.Equal("original", await File.ReadAllTextAsync(copy));
        Assert.Equal(Path.Combine(_root, "FileShelf", widgetId.ToString("N"), item.Id.ToString("N"), "report.txt"), copy);
        Assert.False(Path.IsPathRooted(item.StoredRelativePath));
    }

    [Fact]
    public async Task SameNamesUseSeparateOwnedDirectories()
    {
        var firstSource = await CreateSourceAsync("first/report.txt", "first");
        var secondSource = await CreateSourceAsync("second/report.txt", "second");
        var widgetId = Guid.NewGuid();
        var store = new WidgetFileShelfStore(_root);

        var first = await store.AddAsync(widgetId, firstSource, WidgetFileShelfMode.Copy);
        var second = await store.AddAsync(widgetId, secondSource, WidgetFileShelfMode.Copy);

        Assert.Equal(first.Name, second.Name);
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(store.ResolvePath(widgetId, first), store.ResolvePath(widgetId, second));
        Assert.Equal("first", await File.ReadAllTextAsync(store.ResolvePath(widgetId, first)));
        Assert.Equal("second", await File.ReadAllTextAsync(store.ResolvePath(widgetId, second)));
    }

    [Fact]
    public async Task CopyRemovalDeletesOnlyItsOwnedFile()
    {
        var source = await CreateSourceAsync("source/report.txt", "original");
        var widgetId = Guid.NewGuid();
        var store = new WidgetFileShelfStore(_root);
        var item = await store.AddAsync(widgetId, source, WidgetFileShelfMode.Copy);
        var second = await store.AddAsync(widgetId, source, WidgetFileShelfMode.Copy);
        var copy = store.ResolvePath(widgetId, item);
        var unrelated = Path.Combine(Path.GetDirectoryName(copy)!, "keep.txt");
        await File.WriteAllTextAsync(unrelated, "keep");

        store.Remove(widgetId, item);

        Assert.False(File.Exists(copy));
        Assert.Equal("original", await File.ReadAllTextAsync(source));
        Assert.Equal("keep", await File.ReadAllTextAsync(unrelated));
        Assert.Equal("original", await File.ReadAllTextAsync(store.ResolvePath(widgetId, second)));
        store.Remove(widgetId, item);
    }

    [Theory]
    [InlineData(WidgetFileShelfMode.Reference)]
    [InlineData(WidgetFileShelfMode.Copy)]
    public async Task DirectoriesAreRejectedWithFileOnlyExplanation(WidgetFileShelfMode mode)
    {
        Directory.CreateDirectory(_root);

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            new WidgetFileShelfStore(_root).AddAsync(Guid.NewGuid(), _root, mode));

        Assert.Contains("仅支持文件", exception.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "FileShelf")));
    }

    [Fact]
    public async Task MissingSourceIsRejectedWithoutCreatingCopy()
    {
        var store = new WidgetFileShelfStore(_root);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            store.AddAsync(Guid.NewGuid(), Path.Combine(_root, "missing.txt"), WidgetFileShelfMode.Copy));

        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task AlreadyCancelledImportCreatesNoCopy()
    {
        var source = await CreateSourceAsync("source/report.txt", "original");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WidgetFileShelfStore(_root).AddAsync(Guid.NewGuid(), source, WidgetFileShelfMode.Copy, cancellation.Token));

        Assert.False(Directory.Exists(Path.Combine(_root, "FileShelf")));
        Assert.Equal("original", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task CancellationDuringCopyCleansUpTheCreatedFile()
    {
        var source = await CreateSourceAsync("source/large.bin", string.Empty);
        using (var stream = File.OpenWrite(source)) stream.SetLength(256L * 1024 * 1024);
        var store = new WidgetFileShelfStore(_root);
        var widgetId = Guid.NewGuid();
        var directory = Path.Combine(_root, "FileShelf", widgetId.ToString("N"));
        using var cancellation = new CancellationTokenSource();
        var importing = store.AddAsync(widgetId, source, WidgetFileShelfMode.Copy, cancellation.Token);
        var waitUntilCreated = Task.Run(async () =>
        {
            while (!Directory.Exists(directory) || !Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any())
            {
                Assert.False(importing.IsCompleted, "The import finished before cancellation could observe its destination.");
                await Task.Delay(1);
            }
            cancellation.Cancel();
        });
        await waitUntilCreated.WaitAsync(TimeSpan.FromSeconds(15));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importing);

        Assert.Empty(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories));
        Assert.Equal(256L * 1024 * 1024, new FileInfo(source).Length);
    }

    [Fact]
    public async Task FailedImportPreservesSourceAndLeavesNoPartialFile()
    {
        var source = await CreateSourceAsync("source/report.txt", "original");
        var store = new WidgetFileShelfStore(_root);
        using (var locked = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => store.AddAsync(Guid.NewGuid(), source, WidgetFileShelfMode.Copy));
        }

        Assert.Equal("original", await File.ReadAllTextAsync(source));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "FileShelf"), "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("traversal")]
    [InlineData("absolute")]
    [InlineData("other-item")]
    [InlineData("other-widget")]
    [InlineData("different-name")]
    public async Task TamperedCopyPathsCannotResolveOrDeleteFiles(string tampering)
    {
        var source = await CreateSourceAsync("source/report.txt", "original");
        var store = new WidgetFileShelfStore(_root);
        var widgetId = Guid.NewGuid();
        var item = await store.AddAsync(widgetId, source, WidgetFileShelfMode.Copy);
        var other = await store.AddAsync(widgetId, source, WidgetFileShelfMode.Copy);
        var originalCopyPath = store.ResolvePath(widgetId, item);
        item.StoredRelativePath = tampering switch
        {
            "traversal" => Path.Combine("FileShelf", widgetId.ToString("N"), item.Id.ToString("N"), "..", "..", "..", "..", "source", "report.txt"),
            "absolute" => source,
            "other-item" => other.StoredRelativePath,
            "other-widget" => Path.Combine("FileShelf", Guid.NewGuid().ToString("N"), item.Id.ToString("N"), item.Name),
            _ => Path.Combine("FileShelf", widgetId.ToString("N"), item.Id.ToString("N"), "keep.txt")
        };

        Assert.Throws<InvalidDataException>(() => store.ResolvePath(widgetId, item));
        Assert.Throws<InvalidDataException>(() => store.Remove(widgetId, item));

        Assert.Equal("original", await File.ReadAllTextAsync(source));
        Assert.Equal("original", await File.ReadAllTextAsync(originalCopyPath));
        Assert.Equal("original", await File.ReadAllTextAsync(store.ResolvePath(widgetId, other)));
    }

    [Fact]
    public async Task RemovalRejectsDirectoryJunctionAtFilePath()
    {
        var source = await CreateSourceAsync("source/report.txt", "original");
        var store = new WidgetFileShelfStore(_root);
        var widgetId = Guid.NewGuid();
        var item = await store.AddAsync(widgetId, source, WidgetFileShelfMode.Copy);
        var copy = store.ResolvePath(widgetId, item);
        File.Delete(copy);
        CreateDirectoryJunction(copy, Path.GetDirectoryName(source)!);
        try
        {
            Assert.Throws<InvalidDataException>(() => store.Remove(widgetId, item));
            Assert.Equal("original", await File.ReadAllTextAsync(source));
            Assert.True(Directory.Exists(copy));
        }
        finally { Directory.Delete(copy); }
    }

    [Fact]
    public async Task RemovalRejectsJunctionItemDirectories()
    {
        var source = await CreateSourceAsync("source/report.txt", "original");
        var store = new WidgetFileShelfStore(_root);
        var widgetId = Guid.NewGuid();
        var item = await store.AddAsync(widgetId, source, WidgetFileShelfMode.Copy);
        var copy = store.ResolvePath(widgetId, item);
        var itemDirectory = Path.GetDirectoryName(copy)!;
        var movedDirectory = Path.Combine(_root, "unrelated");
        Directory.Move(itemDirectory, movedDirectory);
        CreateDirectoryJunction(itemDirectory, movedDirectory);
        try
        {
            Assert.Throws<InvalidDataException>(() => store.Remove(widgetId, item));
            Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(movedDirectory, item.Name)));
            Assert.Equal("original", await File.ReadAllTextAsync(source));
        }
        finally { Directory.Delete(itemDirectory); }
    }

    [Fact]
    public async Task TrustedConfigurationDirectoryMayUseAJunction()
    {
        var source = await CreateSourceAsync("source/report.txt", "original");
        var actualDirectory = Path.Combine(_root, "actual-config");
        var linkedDirectory = Path.Combine(_root, "linked-config");
        Directory.CreateDirectory(actualDirectory);
        CreateDirectoryJunction(linkedDirectory, actualDirectory);
        try
        {
            var store = new WidgetFileShelfStore(linkedDirectory);
            var widgetId = Guid.NewGuid();
            var item = await store.AddAsync(widgetId, source, WidgetFileShelfMode.Copy);
            var copy = store.ResolvePath(widgetId, item);

            Assert.Equal("original", await File.ReadAllTextAsync(copy));
            store.Remove(widgetId, item);
            Assert.False(File.Exists(copy));
            Assert.True(File.Exists(source));
        }
        finally { Directory.Delete(linkedDirectory); }
    }

    [Fact]
    public async Task MixedModesAndMissingItemsSurviveSaveAndReload()
    {
        var source = await CreateSourceAsync("source/report.txt", "original");
        var store = new WidgetFileShelfStore(_root);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.FileShelf, FileShelfBusy = true, FileShelfStatus = "temporary" };
        var reference = await store.AddAsync(widget.Id, source, WidgetFileShelfMode.Reference);
        var copy = await store.AddAsync(widget.Id, source, WidgetFileShelfMode.Copy);
        widget.FileShelfItems.AddRange([reference, copy]);
        widget.FileShelfMode = WidgetFileShelfMode.Copy;
        File.Delete(source);
        var layout = new JsonLayoutStore(_root);

        await layout.SaveAsync(new() { Widgets = [widget] });
        var loaded = Assert.Single((await layout.LoadAsync()).Widgets);

        Assert.Equal(widget.Id, loaded.Id);
        Assert.Equal(WidgetFileShelfMode.Copy, loaded.FileShelfMode);
        Assert.Equal(2, loaded.FileShelfItems.Count);
        Assert.Equal(WidgetFileShelfMode.Reference, loaded.FileShelfItems[0].Mode);
        Assert.Equal(WidgetFileShelfMode.Copy, loaded.FileShelfItems[1].Mode);
        Assert.Equal(reference.Id, loaded.FileShelfItems[0].Id);
        Assert.Equal(copy.StoredRelativePath, loaded.FileShelfItems[1].StoredRelativePath);
        Assert.Equal(copy.AddedAt, loaded.FileShelfItems[1].AddedAt);
        Assert.Null(loaded.FileShelfItems[0].IsAvailable);
        Assert.Null(loaded.FileShelfItems[1].IsAvailable);
        Assert.False(loaded.FileShelfBusy);
        Assert.Empty(loaded.FileShelfStatus);
        Assert.Equal("original", await File.ReadAllTextAsync(store.ResolvePath(loaded.Id, loaded.FileShelfItems[1])));
        var json = await File.ReadAllTextAsync(layout.StatePath);
        Assert.DoesNotContain("FileShelfBusy", json);
        Assert.DoesNotContain("FileShelfStatus", json);
        Assert.DoesNotContain("IsAvailable", json);
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.GetProperty("Widgets")[0].GetProperty("FileShelfItems")[0].TryGetProperty("Name", out _));
    }

    [Fact]
    public void NormalizationRepairsMetadataWithoutDiscardingMissingFiles()
    {
        var duplicateId = Guid.NewGuid();
        var widget = JsonSerializer.Deserialize<DesktopWidget>($$"""
            { "Kind": 10, "FileShelfMode": 99, "FileShelfStatus": null, "FileShelfItems": [
              null,
              { "Id": "00000000-0000-0000-0000-000000000000", "OriginalPath": null, "StoredRelativePath": null, "Mode": 99 },
              { "Id": "{{duplicateId}}", "OriginalPath": "missing-reference.txt", "Mode": 0 },
              { "Id": "{{duplicateId}}", "OriginalPath": "missing-copy.txt", "StoredRelativePath": "missing", "Mode": 1 }
            ] }
            """)!;

        DesktopWidgets.Normalize(new() { Widgets = [widget] });

        Assert.Equal(WidgetFileShelfMode.Reference, widget.FileShelfMode);
        Assert.Equal(3, widget.FileShelfItems.Count);
        Assert.Equal(3, widget.FileShelfItems.Select(item => item.Id).Distinct().Count());
        Assert.All(widget.FileShelfItems, item => Assert.NotEqual(Guid.Empty, item.Id));
        Assert.Empty(widget.FileShelfItems[0].OriginalPath);
        Assert.Empty(widget.FileShelfItems[0].StoredRelativePath);
        Assert.Empty(widget.FileShelfItems[0].Name);
        Assert.Equal(WidgetFileShelfMode.Reference, widget.FileShelfItems[0].Mode);
        Assert.Equal("missing-reference.txt", widget.FileShelfItems[1].Name);
        Assert.Equal("missing-copy.txt", widget.FileShelfItems[2].Name);
        Assert.Equal(WidgetFileShelfMode.Copy, widget.FileShelfItems[2].Mode);
        Assert.All(widget.FileShelfItems, item => Assert.Null(item.IsAvailable));
        Assert.Empty(widget.FileShelfStatus);
        Assert.False(Directory.Exists(_root));
        widget.FileShelfItems = null!;
        DesktopWidgets.Normalize(new() { Widgets = [widget] });
        Assert.Empty(widget.FileShelfItems);
    }

    [Fact]
    public void FileShelfIsAppendedAndUsesTheSupportedCardSizes()
    {
        Assert.Equal(new[] { DesktopWidgetKind.Clock, DesktopWidgetKind.Calendar, DesktopWidgetKind.Note,
            DesktopWidgetKind.Todo, DesktopWidgetKind.Translation, DesktopWidgetKind.Weather,
            DesktopWidgetKind.Performance, DesktopWidgetKind.Network, DesktopWidgetKind.ControlCenter,
            DesktopWidgetKind.Lyrics, DesktopWidgetKind.FileShelf }, Enum.GetValues<DesktopWidgetKind>());
        Assert.Equal(Enumerable.Range(0, 11), Enum.GetValues<DesktopWidgetKind>().Select(kind => (int)kind));
        Assert.Equal("文件中转站", DesktopWidgets.GetName(DesktopWidgetKind.FileShelf));
        Assert.Equal(DesktopWidgetSize.Large, DesktopWidgets.DefaultSize(DesktopWidgetKind.FileShelf));
        Assert.Equal(new[] { DesktopWidgetSize.Medium, DesktopWidgetSize.Large }, DesktopWidgets.SupportedSizes(DesktopWidgetKind.FileShelf));
        Assert.Equal(WidgetFileShelfMode.Reference, new DesktopWidget().FileShelfMode);
    }

    private static void CreateDirectoryJunction(string linkPath, string targetPath)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "/c", "mklink", "/J", linkPath, targetPath }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"Could not create a junction: {output} {error}");
        Assert.True((File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0);
    }

    private async Task<string> CreateSourceAsync(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
