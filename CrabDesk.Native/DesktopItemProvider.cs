using CrabDesk.Core;
using System.IO;

namespace CrabDesk.Native;

public sealed class DesktopItemProvider : IDesktopItemProvider
{
    private readonly string[] _desktopDirectories;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly System.Threading.Timer _changeTimer;
    private bool _disposed;

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

        _changeTimer = new System.Threading.Timer(_ => ItemsChanged?.Invoke(this, EventArgs.Empty));
        foreach (var directory in _desktopDirectories)
        {
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
            };
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnChanged;
            watcher.Changed += OnChanged;
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
                            if (IsDesktopMetadataFile(fullPath))
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

    private void OnChanged(object sender, FileSystemEventArgs args)
    {
        _changeTimer.Change(250, Timeout.Infinite);
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
    /// Used by the runtime to detect changes made in Windows' Desktop Icon
    /// Settings dialog without treating a registry write as a filesystem
    /// change.
    /// </summary>
    public static string GetSystemDesktopIconVisibilitySignature() => string.Join(
        ";",
        StandardSystemItems.Select(item =>
            $"{item.Clsid}:{(DesktopSystemIconVisibility.IsVisible(item.Clsid) ? 1 : 0)}"));

    private static bool IsDesktopMetadataFile(string path) =>
        string.Equals(Path.GetFileName(path), "desktop.ini", StringComparison.OrdinalIgnoreCase);

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
