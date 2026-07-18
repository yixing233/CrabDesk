using System.Text.Json;

namespace CrabDesk.Core;

public sealed record LayoutBackupInfo(
    string Path,
    DateTimeOffset CreatedAt,
    int SchemaVersion,
    int BoxCount,
    int RuleCount,
    int MonitorCount,
    LayoutBackupSnapshot Snapshot)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public string CreatedAtText => CreatedAt.ToString("yyyy/MM/dd  HH:mm");
    public int IconCount => Snapshot.IconPositions?.Count ?? 0;
    public bool HasWallpaper => !string.IsNullOrWhiteSpace(Snapshot.WallpaperPath);
}

public sealed record LayoutBackupSnapshot(
    LayoutRect DesktopBounds,
    IReadOnlyList<LayoutBackupBoxSnapshot> Boxes,
    IReadOnlyList<DesktopIconPositionSnapshot>? IconPositions = null,
    string WallpaperPath = "");

public sealed record DesktopBackupCapture(
    IReadOnlyList<DesktopIconPositionSnapshot> IconPositions,
    string WallpaperPath);

public sealed record LayoutBackupDocument(
    CrabDeskState State,
    LayoutBackupSnapshot Snapshot);

public sealed record LayoutBackupBoxSnapshot(
    string Title,
    LayoutRect Bounds,
    string Background,
    string Accent,
    bool IsCollapsed);

public sealed class JsonBackupService : IBackupService
{
    public JsonBackupService(string backupDirectory)
    {
        BackupDirectory = System.IO.Path.GetFullPath(backupDirectory);
        Directory.CreateDirectory(BackupDirectory);
    }

    public string BackupDirectory { get; }

    public async Task<LayoutBackupInfo> CreateAsync(
        CrabDeskState state,
        DesktopBackupCapture? desktopCapture = null,
        CancellationToken cancellationToken = default)
    {
        var path = System.IO.Path.Combine(
            BackupDirectory,
            $"CrabDesk-{DateTime.Now:yyyyMMdd-HHmmssfff}.crabdesk.json");
        await WriteAtomicAsync(state, path, desktopCapture, cancellationToken).ConfigureAwait(false);
        return CreateInfo(state, path, File.GetLastWriteTimeUtc(path), desktopCapture);
    }

    public Task ExportAsync(
        CrabDeskState state,
        string destinationPath,
        DesktopBackupCapture? desktopCapture = null,
        CancellationToken cancellationToken = default) =>
        WriteAtomicAsync(state, destinationPath, desktopCapture, cancellationToken);

    public async Task<CrabDeskState> LoadAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        (await LoadDocumentAsync(path, cancellationToken).ConfigureAwait(false)).State;

    public async Task<LayoutBackupDocument> LoadDocumentAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            true);
        using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        LayoutBackupDocument? package = null;
        if (document.RootElement.TryGetProperty("State", out _))
        {
            package = document.RootElement.Deserialize<LayoutBackupDocument>(JsonLayoutStore.SerializerOptions);
        }

        if (package is null)
        {
            var state = document.RootElement.Deserialize<CrabDeskState>(JsonLayoutStore.SerializerOptions)
                ?? throw new InvalidDataException("Backup does not contain a valid layout.");
            package = new LayoutBackupDocument(state, CreateSnapshot(state.Boxes, null));
        }

        JsonLayoutStore.NormalizeState(package.State);
        return package;
    }

    public async Task<IReadOnlyList<LayoutBackupInfo>> GetBackupsAsync(
        CancellationToken cancellationToken = default)
    {
        var backups = new List<LayoutBackupInfo>();
        foreach (var path in Directory.EnumerateFiles(BackupDirectory, "*.crabdesk.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var package = await LoadDocumentAsync(path, cancellationToken).ConfigureAwait(false);
                backups.Add(CreateInfo(
                    package.State,
                    path,
                    File.GetLastWriteTimeUtc(path),
                    package.Snapshot));
            }
            catch (JsonException)
            {
            }
            catch (InvalidDataException)
            {
            }
            catch (IOException)
            {
            }
        }
        return backups.OrderByDescending(backup => backup.CreatedAt).ToArray();
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = EnsurePathInsideBackupDirectory(path);
        File.Delete(fullPath);
        return Task.CompletedTask;
    }

    public async Task CleanupAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        retentionDays = Math.Clamp(retentionDays, 1, 365);
        var backups = await GetBackupsAsync(cancellationToken).ConfigureAwait(false);
        var cutoff = DateTimeOffset.Now.AddDays(-retentionDays);
        foreach (var backup in backups.Skip(1).Where(backup => backup.CreatedAt < cutoff))
        {
            await DeleteAsync(backup.Path, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteAtomicAsync(
        CrabDeskState state,
        string destinationPath,
        DesktopBackupCapture? desktopCapture,
        CancellationToken cancellationToken)
    {
        JsonLayoutStore.NormalizeState(state);
        var fullPath = System.IO.Path.GetFullPath(destinationPath);
        var directory = System.IO.Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Invalid backup path.");
        Directory.CreateDirectory(directory);
        var tempPath = fullPath + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                true))
            {
                var package = new LayoutBackupDocument(state, CreateSnapshot(state.Boxes, desktopCapture));
                await JsonSerializer.SerializeAsync(
                        stream,
                        package,
                        JsonLayoutStore.SerializerOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(tempPath, fullPath, true);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    private string EnsurePathInsideBackupDirectory(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var root = BackupDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Backup path is outside the backup directory.");
        }
        return fullPath;
    }

    private static LayoutBackupInfo CreateInfo(
        CrabDeskState state,
        string path,
        DateTime createdAt,
        DesktopBackupCapture? desktopCapture) =>
        CreateInfo(state, path, createdAt, CreateSnapshot(state.Boxes, desktopCapture));

    private static LayoutBackupInfo CreateInfo(
        CrabDeskState state,
        string path,
        DateTime createdAt,
        LayoutBackupSnapshot snapshot) => new(
        path,
        new DateTimeOffset(DateTime.SpecifyKind(createdAt, DateTimeKind.Utc)).ToLocalTime(),
        state.SchemaVersion,
        state.Boxes.Count,
        state.OrganizationRules.Count,
        state.Boxes.Select(box => box.MonitorId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .Count(),
        snapshot);

    private static LayoutBackupSnapshot CreateSnapshot(
        IReadOnlyList<DesktopBox> boxes,
        DesktopBackupCapture? desktopCapture)
    {
        const double defaultDesktopWidth = 1920;
        const double defaultDesktopHeight = 1080;
        var positions = desktopCapture?.IconPositions ?? [];
        var minX = Math.Min(0, Math.Min(
            boxes.Count == 0 ? 0 : boxes.Min(box => box.Bounds.X),
            positions.Count == 0 ? 0 : positions.Min(position => position.X)));
        var minY = Math.Min(0, Math.Min(
            boxes.Count == 0 ? 0 : boxes.Min(box => box.Bounds.Y),
            positions.Count == 0 ? 0 : positions.Min(position => position.Y)));
        var maxX = Math.Max(defaultDesktopWidth, Math.Max(
            boxes.Count == 0 ? defaultDesktopWidth : boxes.Max(box => box.Bounds.X + box.Bounds.Width),
            positions.Count == 0 ? defaultDesktopWidth : positions.Max(position => position.X + 96)));
        var maxY = Math.Max(defaultDesktopHeight, Math.Max(
            boxes.Count == 0 ? defaultDesktopHeight : boxes.Max(box => box.Bounds.Y + box.Bounds.Height),
            positions.Count == 0 ? defaultDesktopHeight : positions.Max(position => position.Y + 96)));
        var desktopBounds = new LayoutRect(minX, minY, maxX - minX, maxY - minY);
        var snapshotBoxes = boxes.Select(box => new LayoutBackupBoxSnapshot(
            box.Title,
            box.Bounds,
            box.Appearance.Background,
            box.Appearance.Accent,
            box.IsCollapsed)).ToArray();
        return new LayoutBackupSnapshot(
            desktopBounds,
            snapshotBoxes,
            positions.ToArray(),
            desktopCapture?.WallpaperPath ?? string.Empty);
    }
}
