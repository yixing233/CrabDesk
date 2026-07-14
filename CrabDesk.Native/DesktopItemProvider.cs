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
                        var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                        var extension = Path.GetExtension(fullPath);
                        items.Add(new DesktopItemRef
                        {
                            Key = new DesktopItemKey("file", FileIdentity.GetStableId(fullPath)),
                            DisplayName = Path.GetFileNameWithoutExtension(fullPath),
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
            }

            items.AddRange(SystemItems);
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

    private static IReadOnlyList<DesktopItemRef> SystemItems =>
    [
        Shell("回收站", "shell:::{645FF040-5081-101B-9F08-00AA002F954E}"),
        Shell("此电脑", "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"),
        Shell("用户文件", "shell:::{59031A47-3F72-44A7-89C5-5595FE6B30EE}"),
        Shell("网络", "shell:::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}"),
        Shell("控制面板", "shell:::{26EE0668-A00A-44D7-9371-BEB064C98683}")
    ];

    private static DesktopItemRef Shell(string name, string parsingName) => new()
    {
        Key = new DesktopItemKey("shell", parsingName.ToUpperInvariant()),
        DisplayName = name,
        ParsingName = parsingName,
        Kind = DesktopItemKind.Shell,
        IsReadOnly = true
    };
}
