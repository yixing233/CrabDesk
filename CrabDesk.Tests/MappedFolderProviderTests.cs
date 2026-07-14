using CrabDesk.Core;
using CrabDesk.Native;

namespace CrabDesk.Tests;

public sealed class MappedFolderProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CrabDesk.Tests", Guid.NewGuid().ToString("N"));

    public MappedFolderProviderTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task EnumeratesOnlyDirectChildrenWithFileMetadata()
    {
        var childDirectory = Directory.CreateDirectory(Path.Combine(_root, "资料"));
        var filePath = Path.Combine(_root, "说明.txt");
        await File.WriteAllTextAsync(filePath, "CrabDesk");
        await File.WriteAllTextAsync(Path.Combine(childDirectory.FullName, "nested.txt"), "nested");
        using var provider = new MappedFolderProvider();

        var snapshot = await provider.EnumerateAsync(_root);

        Assert.Equal(MappedFolderAvailability.Available, snapshot.Availability);
        Assert.Equal(2, snapshot.Items.Count);
        Assert.Contains(snapshot.Items, item => item.DisplayName == "资料" && item.Kind == DesktopItemKind.Folder);
        Assert.Contains(snapshot.Items, item => item.DisplayName == "说明" && item.Kind == DesktopItemKind.File);
        Assert.DoesNotContain(snapshot.Items, item => item.DisplayName == "nested");
    }

    [Fact]
    public async Task MissingDirectoryReturnsExplicitState()
    {
        using var provider = new MappedFolderProvider();
        var missing = Path.Combine(_root, "missing");

        var snapshot = await provider.EnumerateAsync(missing);

        Assert.Equal(MappedFolderAvailability.Missing, snapshot.Availability);
        Assert.Empty(snapshot.Items);
        Assert.Equal("文件夹不存在", snapshot.Message);
    }

    [Fact]
    public async Task WatcherSignalsDirectChildChanges()
    {
        using var provider = new MappedFolderProvider();
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.ItemsChanged += (_, _) => changed.TrySetResult();
        provider.SetWatchedFolders([_root]);

        await File.WriteAllTextAsync(Path.Combine(_root, "created.txt"), "created");

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
