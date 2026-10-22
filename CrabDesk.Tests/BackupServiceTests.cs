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
        var snapshotBox = Assert.Single(backup.Snapshot.Boxes);
        Assert.Equal(state.Boxes[0].Title, snapshotBox.Title);
        Assert.Equal(state.Boxes[0].Bounds, snapshotBox.Bounds);
        Assert.True(backup.Snapshot.DesktopBounds.Width >= 1920);
        Assert.True(backup.Snapshot.DesktopBounds.Height >= 1080);
        Assert.Equal(23, loaded.SchemaVersion);
        Assert.True(loaded.Settings.Backup.DailyBackup);
        var customRule = Assert.Single(loaded.OrganizationRules.Where(rule =>
            string.IsNullOrEmpty(rule.BuiltInId)));
        Assert.Equal("文档", customRule.Title);
        Assert.Equal(state.Boxes[0].Id, customRule.TargetBoxId);
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
    public async Task DesktopSnapshotPreservesIconPositionsAndWallpaper()
    {
        var service = new JsonBackupService(_root);
        var state = JsonLayoutStore.CreateDefaultState();
        var capture = new DesktopBackupCapture(
            [
                new DesktopIconPositionSnapshot("Chrome", 24, 48),
                new DesktopIconPositionSnapshot("Notes", 180, 126)
            ],
            @"C:\Wallpapers\desk.jpg",
            [137, 80, 78, 71, 13, 10, 26, 10]);

        var backup = await service.CreateAsync(state, capture);
        var document = await service.LoadDocumentAsync(backup.Path);

        Assert.Equal(capture.IconPositions, document.Snapshot.IconPositions);
        Assert.Equal(capture.WallpaperPath, document.Snapshot.WallpaperPath);
        Assert.Equal(2, backup.IconCount);
        Assert.True(backup.HasWallpaper);
        Assert.True(backup.HasDesktopPreview);
        Assert.True(File.Exists(backup.DesktopPreviewPath));
        Assert.Equal(
            capture.DesktopPreviewPng,
            await File.ReadAllBytesAsync(backup.DesktopPreviewPath));
        Assert.Equal(
            Path.GetFileName(backup.DesktopPreviewPath),
            document.Snapshot.DesktopPreviewFileName);
        Assert.True(document.Snapshot.DesktopBounds.Width >= 276);
        Assert.True(document.Snapshot.DesktopBounds.Height >= 222);
    }

    [Fact]
    public async Task LegacyDesktopImageKeepsItsSavedBoxesAvailableForAnOverlay()
    {
        var service = new JsonBackupService(_root);
        var state = JsonLayoutStore.CreateDefaultState();
        state.Boxes.Add(new DesktopBox { Title = "工具", Bounds = new LayoutRect(100, 80, 300, 200) });
        var snapshotBox = new LayoutBackupBoxSnapshot("工具", state.Boxes[0].Bounds, "#FF202020", "#FF4080FF", true);
        var legacy = new
        {
            State = state,
            Snapshot = new
            {
                DesktopBounds = new LayoutRect(0, 0, 1920, 1080),
                Boxes = new[] { snapshotBox },
                DesktopPreviewFileName = "legacy.preview.png"
            }
        };
        await File.WriteAllTextAsync(Path.Combine(_root, "legacy.crabdesk.json"), System.Text.Json.JsonSerializer.Serialize(legacy));
        await File.WriteAllBytesAsync(Path.Combine(_root, "legacy.preview.png"), [137, 80, 78, 71]);

        var backup = Assert.Single(await service.GetBackupsAsync());

        Assert.True(backup.HasDesktopPreview);
        Assert.False(backup.HasCompleteDesktopPreview);
        Assert.True(backup.Snapshot.NeedsBoxOverlay);
        Assert.Equal(snapshotBox, Assert.Single(backup.Snapshot.Boxes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoxLayerCaptureMetadataSurvivesBackupRoundTrip(bool includesBoxes)
    {
        var service = new JsonBackupService(_root);
        var state = JsonLayoutStore.CreateDefaultState();
        state.Boxes.Add(new DesktopBox { Title = "工具" });
        var capture = new DesktopBackupCapture([], "", [137, 80, 78, 71], includesBoxes);

        var backup = await service.CreateAsync(state, capture);
        var document = await service.LoadDocumentAsync(backup.Path);

        Assert.Equal(includesBoxes, document.Snapshot.DesktopPreviewIncludesBoxes);
        Assert.Equal(!includesBoxes, document.Snapshot.NeedsBoxOverlay);
        Assert.Equal(includesBoxes, backup.HasCompleteDesktopPreview);
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
    public async Task DeleteRemovesTheAssociatedDesktopPreview()
    {
        var service = new JsonBackupService(_root);
        var backup = await service.CreateAsync(
            JsonLayoutStore.CreateDefaultState(),
            new DesktopBackupCapture([], "", [137, 80, 78, 71]));

        await service.DeleteAsync(backup.Path);

        Assert.False(File.Exists(backup.Path));
        Assert.False(File.Exists(backup.DesktopPreviewPath));
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
