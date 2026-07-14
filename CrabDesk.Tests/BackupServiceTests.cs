using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class BackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CrabDesk.BackupTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreateAndLoadPreservesLayoutRulesAndSettings()
    {
        var service = new JsonBackupService(_root);
        var state = JsonLayoutStore.CreateDefaultState("display-1");
        state.Boxes.Add(new DesktopBox { Title = "文档", MonitorId = "display-1" });
        state.Settings.Backup.DailyBackup = true;
        state.OrganizationRules.Add(new OrganizationRule
        {
            Title = "文档",
            Extensions = ["pdf"],
            TargetBoxId = state.Boxes[0].Id
        });

        var backup = await service.CreateAsync(state);
        var loaded = await service.LoadAsync(backup.Path);

        Assert.True(File.Exists(backup.Path));
        Assert.Equal(14, loaded.SchemaVersion);
        Assert.True(loaded.Settings.Backup.DailyBackup);
        Assert.Equal("文档", loaded.OrganizationRules[0].Title);
        Assert.Equal(state.Boxes[0].Id, loaded.OrganizationRules[0].TargetBoxId);
    }

    [Fact]
    public async Task ExportUsesAtomicReplacementAndCanBeImported()
    {
        var service = new JsonBackupService(_root);
        var destination = Path.Combine(_root, "export.crabdesk.json");
        var state = JsonLayoutStore.CreateDefaultState();
        state.Boxes.Add(new DesktopBox { Title = "测试", MonitorId = "primary" });

        await service.ExportAsync(state, destination);
        state.Boxes[0].Title = "更新后的布局";
        await service.ExportAsync(state, destination);
        var loaded = await service.LoadAsync(destination);

        Assert.Equal("更新后的布局", loaded.Boxes[0].Title);
        Assert.False(File.Exists(destination + ".tmp"));
    }

    [Fact]
    public async Task EnumerationSkipsCorruptBackups()
    {
        var service = new JsonBackupService(_root);
        await service.CreateAsync(JsonLayoutStore.CreateDefaultState());
        await File.WriteAllTextAsync(Path.Combine(_root, "broken.crabdesk.json"), "not json");

        var backups = await service.GetBackupsAsync();

        Assert.Single(backups);
    }

    [Fact]
    public async Task DeleteRejectsPathsOutsideBackupDirectory()
    {
        var service = new JsonBackupService(_root);
        var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.crabdesk.json");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(outside));
    }

    [Fact]
    public async Task CleanupKeepsNewestBackupEvenWhenAllAreExpired()
    {
        var service = new JsonBackupService(_root);
        var first = await service.CreateAsync(JsonLayoutStore.CreateDefaultState());
        await Task.Delay(20);
        var second = await service.CreateAsync(JsonLayoutStore.CreateDefaultState());
        File.SetLastWriteTimeUtc(first.Path, DateTime.UtcNow.AddDays(-10));
        File.SetLastWriteTimeUtc(second.Path, DateTime.UtcNow.AddDays(-9));

        await service.CleanupAsync(1);
        var remaining = await service.GetBackupsAsync();

        var backup = Assert.Single(remaining);
        Assert.Equal(second.Path, backup.Path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
