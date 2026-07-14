using System.Text.Json;

namespace CrabDesk.Core;

public sealed record LayoutBackupInfo(
    string Path,
    DateTimeOffset CreatedAt,
    int SchemaVersion,
    int BoxCount,
    int RuleCount,
    int MonitorCount);

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
        CancellationToken cancellationToken = default)
    {
        var path = System.IO.Path.Combine(
            BackupDirectory,
            $"CrabDesk-{DateTime.Now:yyyyMMdd-HHmmssfff}.crabdesk.json");
        await WriteAtomicAsync(state, path, cancellationToken).ConfigureAwait(false);
        return CreateInfo(state, path, File.GetLastWriteTimeUtc(path));
    }

    public Task ExportAsync(
        CrabDeskState state,
        string destinationPath,
        CancellationToken cancellationToken = default) =>
        WriteAtomicAsync(state, destinationPath, cancellationToken);

    public async Task<CrabDeskState> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            true);
        var state = await JsonSerializer.DeserializeAsync<CrabDeskState>(
                stream,
                JsonLayoutStore.SerializerOptions,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("备份文件不包含有效布局。");
        JsonLayoutStore.NormalizeState(state);
        return state;
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
                var state = await LoadAsync(path, cancellationToken).ConfigureAwait(false);
                backups.Add(CreateInfo(state, path, File.GetLastWriteTimeUtc(path)));
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
        CancellationToken cancellationToken)
    {
        JsonLayoutStore.NormalizeState(state);
        var fullPath = System.IO.Path.GetFullPath(destinationPath);
        var directory = System.IO.Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("备份路径无效。");
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
                await JsonSerializer.SerializeAsync(
                        stream,
                        state,
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
            throw new InvalidOperationException("只能删除 CrabDesk 备份目录中的文件。");
        }
        return fullPath;
    }

    private static LayoutBackupInfo CreateInfo(CrabDeskState state, string path, DateTime createdAt) => new(
        path,
        new DateTimeOffset(DateTime.SpecifyKind(createdAt, DateTimeKind.Utc)).ToLocalTime(),
        state.SchemaVersion,
        state.Boxes.Count,
        state.OrganizationRules.Count,
        state.Boxes.Select(box => box.MonitorId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().Count());
}
