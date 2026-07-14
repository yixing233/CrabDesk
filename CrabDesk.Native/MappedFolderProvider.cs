using CrabDesk.Core;
using System.IO;

namespace CrabDesk.Native;

public sealed class MappedFolderProvider : IMappedFolderProvider
{
    private readonly object _sync = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Timer _changeTimer;
    private bool _disposed;

    public MappedFolderProvider()
    {
        _changeTimer = new System.Threading.Timer(_ => ItemsChanged?.Invoke(this, EventArgs.Empty));
    }

    public event EventHandler? ItemsChanged;

    public Task<MappedFolderSnapshot> EnumerateAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Enumerate(path, cancellationToken), cancellationToken);
    }

    public void SetWatchedFolders(IEnumerable<string> paths)
    {
        var requested = paths
            .Select(TryNormalizePath)
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        lock (_sync)
        {
            ThrowIfDisposed();
            foreach (var path in _watchers.Keys.Where(path => !requested.Contains(path)).ToArray())
            {
                RemoveWatcher(path);
            }

            foreach (var path in requested.Where(path => !_watchers.ContainsKey(path) && Directory.Exists(path)))
            {
                TryAddWatcher(path);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }
            _watchers.Clear();
            _changeTimer.Dispose();
        }
    }

    private static MappedFolderSnapshot Enumerate(string path, CancellationToken cancellationToken)
    {
        var normalized = TryNormalizePath(path);
        if (normalized is null)
        {
            return new MappedFolderSnapshot(
                path,
                MappedFolderAvailability.Error,
                [],
                "文件夹路径无效");
        }

        if (!Directory.Exists(normalized))
        {
            var availability = IsStorageOffline(normalized)
                ? MappedFolderAvailability.Offline
                : MappedFolderAvailability.Missing;
            return new MappedFolderSnapshot(
                normalized,
                availability,
                [],
                availability == MappedFolderAvailability.Offline ? "磁盘或网络位置不可用" : "文件夹不存在");
        }

        try
        {
            var items = new List<DesktopItemRef>();
            foreach (var entry in Directory.EnumerateFileSystemEntries(normalized))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var fullPath = Path.GetFullPath(entry);
                    var attributes = File.GetAttributes(fullPath);
                    var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                    var extension = Path.GetExtension(fullPath);
                    items.Add(new DesktopItemRef
                    {
                        Key = new DesktopItemKey("file", FileIdentity.GetStableId(fullPath)),
                        DisplayName = isDirectory
                            ? Path.GetFileName(fullPath)
                            : Path.GetFileNameWithoutExtension(fullPath),
                        ParsingName = fullPath,
                        FileSystemPath = fullPath,
                        Kind = isDirectory
                            ? DesktopItemKind.Folder
                            : extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                                ? DesktopItemKind.Shortcut
                                : DesktopItemKind.File,
                        ModifiedAt = isDirectory
                            ? Directory.GetLastWriteTimeUtc(fullPath)
                            : File.GetLastWriteTimeUtc(fullPath),
                        IsReadOnly = attributes.HasFlag(FileAttributes.ReadOnly)
                    });
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return new MappedFolderSnapshot(normalized, MappedFolderAvailability.Available, items);
        }
        catch (UnauthorizedAccessException)
        {
            return new MappedFolderSnapshot(
                normalized,
                MappedFolderAvailability.AccessDenied,
                [],
                "没有访问此文件夹的权限");
        }
        catch (IOException exception)
        {
            return new MappedFolderSnapshot(
                normalized,
                IsStorageOffline(normalized) ? MappedFolderAvailability.Offline : MappedFolderAvailability.Error,
                [],
                exception.Message);
        }
    }

    private void TryAddWatcher(string path)
    {
        try
        {
            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                InternalBufferSize = 16 * 1024
            };
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnChanged;
            watcher.Changed += OnChanged;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;
            _watchers[path] = watcher;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs eventArgs)
    {
        lock (_sync)
        {
            if (!_disposed)
            {
                _changeTimer.Change(250, Timeout.Infinite);
            }
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs eventArgs)
    {
        lock (_sync)
        {
            if (sender is FileSystemWatcher watcher)
            {
                var path = _watchers.FirstOrDefault(pair => ReferenceEquals(pair.Value, watcher)).Key;
                if (!string.IsNullOrEmpty(path))
                {
                    RemoveWatcher(path);
                }
            }
            if (!_disposed)
            {
                _changeTimer.Change(250, Timeout.Infinite);
            }
        }
    }

    private void RemoveWatcher(string path)
    {
        if (_watchers.Remove(path, out var watcher))
        {
            watcher.Dispose();
        }
    }

    private static string? TryNormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsStorageOffline(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
            {
                return false;
            }
            if (root.StartsWith("\\\\", StringComparison.Ordinal))
            {
                return !Directory.Exists(root);
            }
            return !new DriveInfo(root).IsReady;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
