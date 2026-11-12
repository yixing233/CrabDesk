namespace CrabDesk.Core;

public sealed class WidgetFileShelfStore
{
    private readonly string _configDirectory;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public WidgetFileShelfStore(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        _configDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configDirectory));
    }

    public Task<WidgetFileShelfItem> AddAsync(Guid widgetId, string sourcePath, WidgetFileShelfMode mode,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => AddCoreAsync(widgetId, sourcePath, mode, cancellationToken), cancellationToken);

    public string ResolvePath(Guid widgetId, WidgetFileShelfItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Mode == WidgetFileShelfMode.Reference) return item.OriginalPath ?? string.Empty;
        if (item.Mode != WidgetFileShelfMode.Copy) throw new InvalidDataException("文件中转站条目的保存方式无效。");
        var expected = ExpectedRelativePath(widgetId, item);
        var saved = (item.StoredRelativePath ?? string.Empty)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(saved) || !string.Equals(saved, expected, PathComparison))
            throw new InvalidDataException("文件中转站副本路径无效，无法访问此条目。");
        return Path.GetFullPath(Path.Combine(_configDirectory, expected));
    }

    public void Remove(Guid widgetId, WidgetFileShelfItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Mode == WidgetFileShelfMode.Reference) return;
        var path = ResolvePath(widgetId, item);
        EnsureSafeOwnedPath(path);
        File.Delete(path);
    }

    private async Task<WidgetFileShelfItem> AddCoreAsync(Guid widgetId, string sourcePath,
        WidgetFileShelfMode mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (widgetId == Guid.Empty) throw new ArgumentException("文件中转站组件标识无效。", nameof(widgetId));
        if (!Enum.IsDefined(mode)) throw new ArgumentException("文件中转站保存方式无效。", nameof(mode));
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var originalPath = Path.GetFullPath(sourcePath);
        if (Directory.Exists(originalPath)) throw new IOException("文件中转站仅支持文件，请选择文件而不是文件夹。");
        if (!File.Exists(originalPath)) throw new FileNotFoundException("所选文件不存在或无法访问。", originalPath);
        var item = new WidgetFileShelfItem
        {
            OriginalPath = originalPath,
            Mode = mode,
            AddedAt = DateTimeOffset.UtcNow,
            IsAvailable = true
        };
        if (mode == WidgetFileShelfMode.Reference)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return item;
        }

        item.StoredRelativePath = ExpectedRelativePath(widgetId, item);
        var ownedRoot = Directory.Exists(_configDirectory)
            ? new DirectoryInfo(_configDirectory).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? _configDirectory
            : _configDirectory;
        var destinationPath = Path.GetFullPath(Path.Combine(ownedRoot, item.StoredRelativePath));
        EnsureSafeOwnedPath(destinationPath, ownedRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        EnsureSafeOwnedPath(destinationPath, ownedRoot);
        var created = false;
        try
        {
            await using var source = new FileStream(originalPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            created = true;
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception exception)
        {
            if (created)
            {
                // Delete only the file this import created; empty GUID directories are harmless.
                try { EnsureSafeOwnedPath(destinationPath, ownedRoot); File.Delete(destinationPath); }
                catch (Exception cleanupException) when (cleanupException is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    throw new IOException($"文件中转站导入未完成，且无法清理临时副本：{destinationPath}",
                        new AggregateException(exception, cleanupException));
                }
            }
            throw;
        }
        return item;
    }

    private static string ExpectedRelativePath(Guid widgetId, WidgetFileShelfItem item)
    {
        var name = item.Name;
        if (widgetId == Guid.Empty || item.Id == Guid.Empty || string.IsNullOrWhiteSpace(name) ||
            name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("文件中转站副本的文件名或标识无效。");
        return Path.Combine("FileShelf", widgetId.ToString("N"), item.Id.ToString("N"), name);
    }

    private void EnsureSafeOwnedPath(string path, string? ownedRoot = null)
    {
        var isFile = true;
        ownedRoot ??= _configDirectory;
        // The configured directory is trusted and may itself use a profile junction.
        for (string? current = path; current is not null &&
             !string.Equals(current, ownedRoot, PathComparison); current = Path.GetDirectoryName(current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("文件中转站副本路径包含链接，无法安全访问此条目。");
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (isFile == isDirectory)
                    throw new InvalidDataException("文件中转站副本路径类型无效。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            isFile = false;
        }
    }
}
