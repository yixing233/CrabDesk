using CrabDesk.Core;
using System.IO;

namespace CrabDesk.Native;

public sealed class DesktopItemProvider : IDesktopItemProvider
{
    private bool _showHiddenFiles;

    /// <summary>
    /// When false (default) enumeration skips Office ~$ lock files and
    /// Hidden/System files, matching Explorer's desktop. Set to true to
    /// surface them on the replacement desktop.
    /// </summary>
    public bool ShowHiddenFiles
    {
        get => _showHiddenFiles;
        set => _showHiddenFiles = value;
    }
    private readonly string[] _desktopDirectories;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly System.Threading.Timer _changeTimer;
    private readonly object _changeSync = new();
    private FileSystemEventArgs? _pendingChange;
    private bool _pendingBurstHasNonRename;
    private bool _disposed;

    /// <summary>
    /// Notifications are coalesced for this long before one ItemsChanged is
    /// raised. A save typically produces several in a row (create, writes,
    /// rename of a temporary file); one snapshot refresh covers them all.
    /// </summary>
    internal const int ChangeQuietPeriodMilliseconds = 250;

    // The default 8 KB buffer overflows on a burst of a few dozen changes,
    // after which every queued notification is discarded silently.
    private const int WatcherBufferSize = 64 * 1024;

    public DesktopItemProvider()
    {
        _desktopDirectories =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        ];
        _desktopDirectories = _desktopDirectories
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _changeTimer = new System.Threading.Timer(_ => FlushPendingChange());
        foreach (var directory in _desktopDirectories)
        {
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = false,
                // Attributes: a file some applications save hidden and reveal
                // once the write completes only becomes a desktop item when
                // the attribute clears; without this filter that is silent.
                NotifyFilter = NotifyFilters.FileName |
                               NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite |
                               NotifyFilters.Attributes,
                InternalBufferSize = WatcherBufferSize
            };
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnChanged;
            watcher.Changed += OnChanged;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    public event EventHandler? ItemsChanged;

    public Task<IReadOnlyList<DesktopItemRef>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<DesktopItemRef>>(() =>
        {
            var items = new List<DesktopItemRef>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in _desktopDirectories)
            {
                try
                {
                    foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var fullPath = Path.GetFullPath(path);
                        if (!seenPaths.Add(fullPath))
                        {
                            continue;
                        }

                        try
                        {
                            var attributes = File.GetAttributes(fullPath);
                            if (IsDesktopMetadataFile(fullPath, _showHiddenFiles))
                            {
                                continue;
                            }
                            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                            var extension = Path.GetExtension(fullPath);
                            items.Add(new DesktopItemRef
                            {
                                Key = new DesktopItemKey("file", FileIdentity.GetStableId(fullPath)),
                                DisplayName = DesktopItemName.GetDisplayName(fullPath, isDirectory),
                                ParsingName = fullPath,
                                FileSystemPath = fullPath,
                                Kind = isDirectory
                                    ? DesktopItemKind.Folder
                                    : extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                                        ? DesktopItemKind.Shortcut
                                        : DesktopItemKind.File,
                                // Explorer's "Modified date" column is
                                // System.DateModified. For regular file
                                // system desktop items that is the last-write
                                // timestamp, independent of icon placement.
                                ModifiedAt = ReadShellModifiedAt(fullPath, isDirectory),
                                CreatedAt = ReadShellCreatedAt(fullPath, isDirectory),
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
                }
                catch (IOException)
                {
                    // A single unavailable desktop directory (offline drive,
                    // cloud placeholder storm) must not fail the whole
                    // enumeration and hide every other item.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            items.AddRange(GetVisibleSystemItems());
            return items;
        }, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
        _changeTimer.Dispose();
    }

    private void OnChanged(object sender, FileSystemEventArgs args) => QueueChange(args);

    private void OnWatcherError(object sender, ErrorEventArgs args)
    {
        // The watcher keeps running after an internal buffer overflow, but the
        // notifications it dropped are gone. Report the directory itself so the
        // runtime takes a full pass and a file saved during the burst still
        // appears. If the watcher stopped altogether, re-arm it.
        if (sender is FileSystemWatcher watcher)
        {
            if (!_disposed && !watcher.EnableRaisingEvents)
            {
                try
                {
                    watcher.EnableRaisingEvents = true;
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                {
                }
            }
            QueueChange(new FileSystemEventArgs(WatcherChangeTypes.All, watcher.Path, null));
        }
    }

    private void QueueChange(FileSystemEventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        lock (_changeSync)
        {
            _pendingChange = args;
            _pendingBurstHasNonRename |= args.ChangeType != WatcherChangeTypes.Renamed;
        }
        try
        {
            _changeTimer.Change(ChangeQuietPeriodMilliseconds, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void FlushPendingChange()
    {
        FileSystemEventArgs? change;
        lock (_changeSync)
        {
            change = _pendingChange;
            var burstHadNonRename = _pendingBurstHasNonRename;
            _pendingChange = null;
            _pendingBurstHasNonRename = false;
            if (change is not null)
            {
                change = DescribeBurst(change, burstHadNonRename);
            }
        }

        if (change is not null && !_disposed)
        {
            ItemsChanged?.Invoke(this, change);
        }
    }

    /// <summary>
    /// Chooses the single notification that stands for a coalesced burst. The
    /// last event wins, except that a burst which also created, deleted or
    /// modified something is reported as a plain change on the final path: a
    /// new file that was written under a temporary name and then renamed must
    /// not look like a mere rename, which the runtime may treat as cosmetic.
    /// </summary>
    internal static FileSystemEventArgs DescribeBurst(FileSystemEventArgs last, bool burstHadNonRename)
    {
        ArgumentNullException.ThrowIfNull(last);
        if (!burstHadNonRename || last.ChangeType != WatcherChangeTypes.Renamed)
        {
            return last;
        }

        return new FileSystemEventArgs(
            WatcherChangeTypes.Changed,
            Path.GetDirectoryName(last.FullPath) ?? string.Empty,
            last.Name);
    }

    private static DateTimeOffset? ReadShellModifiedAt(string path, bool isDirectory)
    {
        try
        {
            var modified = isDirectory
                ? Directory.GetLastWriteTimeUtc(path)
                : File.GetLastWriteTimeUtc(path);
            return modified == DateTime.MinValue ? null : modified;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Explorer's "Date created" column is System.DateCreated, the file
    /// system's creation timestamp. It is read separately from the modified
    /// time because editing a file changes only the latter.
    /// </summary>
    private static DateTimeOffset? ReadShellCreatedAt(string path, bool isDirectory)
    {
        try
        {
            var created = isDirectory
                ? Directory.GetCreationTimeUtc(path)
                : File.GetCreationTimeUtc(path);
            return created == DateTime.MinValue ? null : created;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Used by the runtime to detect changes made in Windows' Desktop Icon
    /// Settings dialog without treating a registry write as a filesystem
    /// change.
    /// </summary>
    public static string GetSystemDesktopIconVisibilitySignature() => string.Join(
        ";",
        StandardSystemItems.Select(item =>
            $"{item.Clsid}:{(DesktopSystemIconVisibility.IsVisible(item.Clsid) ? 1 : 0)}"));

    private static bool IsDesktopMetadataFile(string path, bool showHiddenFiles)
    {
        var fileName = Path.GetFileName(path);
        if (string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // The remaining filters are configurable: Explorer hides Office ~$
        // lock files and Hidden/System files, but the setting can surface them.
        if (showHiddenFiles)
        {
            return false;
        }
        // Office lock files created while a document is open (~$name.docx).
        if (fileName.StartsWith("~$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
            {
                return true;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return false;
    }

    private static IReadOnlyList<DesktopItemRef> GetVisibleSystemItems() =>
        StandardSystemItems
            .Where(item => DesktopSystemIconVisibility.IsVisible(item.Clsid))
            .Select(item => Shell(item.DisplayName, $"shell:::{item.Clsid}"))
            .ToArray();

    private static readonly SystemDesktopItem[] StandardSystemItems =
    [
        new("回收站", "{645FF040-5081-101B-9F08-00AA002F954E}"),
        new("此电脑", "{20D04FE0-3AEA-1069-A2D8-08002B30309D}"),
        new("用户文件", "{59031A47-3F72-44A7-89C5-5595FE6B30EE}"),
        new("网络", "{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}"),
        // The Desktop Icon Settings dialog controls the category root, not
        // the all-tasks namespace ({26EE...}). Using the latter bypasses the
        // checkbox and incorrectly makes Control Panel permanently visible.
        new("控制面板", "{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}")
    ];

    private static DesktopItemRef Shell(string name, string parsingName) => new()
    {
        Key = new DesktopItemKey("shell", parsingName.ToUpperInvariant()),
        DisplayName = name,
        ParsingName = parsingName,
        Kind = DesktopItemKind.Shell,
        IsReadOnly = true
    };

    private readonly record struct SystemDesktopItem(string DisplayName, string Clsid);
}
