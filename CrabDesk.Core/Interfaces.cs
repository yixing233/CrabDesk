namespace CrabDesk.Core;

public interface IDesktopItemProvider : IDisposable
{
    event EventHandler? ItemsChanged;
    Task<IReadOnlyList<DesktopItemRef>> EnumerateAsync(CancellationToken cancellationToken = default);
}

public interface IMappedFolderProvider : IDisposable
{
    event EventHandler? ItemsChanged;
    Task<MappedFolderSnapshot> EnumerateAsync(string path, CancellationToken cancellationToken = default);
    void SetWatchedFolders(IEnumerable<string> paths);
}

public interface IHotkeyService : IDisposable
{
    event EventHandler<GlobalHotkeyPressedEventArgs>? Pressed;
    HotkeyRegistrationStatus Register(HotkeyAction action, HotkeyBinding binding);
    void Unregister(HotkeyAction action);
}

public interface IDesktopContextMenuRegistration
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled, string executablePath);
}

public enum DesktopKeyboardCommand
{
    SelectAll,
    Copy,
    Cut,
    Paste,
    Open,
    Delete,
    Rename
}

public sealed class DesktopKeyboardCommandEventArgs(DesktopKeyboardCommand command) : EventArgs
{
    public DesktopKeyboardCommand Command { get; } = command;
}

public interface IDesktopInputMonitor : IDisposable
{
    event EventHandler<DesktopIconZoomEventArgs>? IconZoomRequested;
    event EventHandler? DesktopSurfaceClicked;
    event EventHandler? DesktopContextMenuRequested;
    event EventHandler? DesktopContextMenuCommandRequested;
    event EventHandler? DesktopContextMenuRefreshRequested;
    event EventHandler? DesktopDeleteRequested;
    event EventHandler? DesktopRenameRequested;
    event EventHandler<DesktopKeyboardCommandEventArgs>? DesktopKeyboardCommandRequested;
    IntPtr DesktopListView { get; set; }
    bool Enabled { get; set; }
    // Lets the low-level wheel hook keep Ctrl+wheel on the unassigned-icon
    // layer when the pointer is over the desktop, and route it to a single
    // box when the pointer is inside one of the box surfaces.
    Func<int, int, bool>? IsPointerOverBox { get; set; }
    // Called by the low-level keyboard hook before it consumes Delete.
    // Explorer keeps its normal behavior while CrabDesk has no custom selection.
    Func<bool>? CanDeleteDesktopItems { get; set; }
    // Called by the low-level keyboard hook before it consumes F2.
    // Explorer keeps its normal behavior while CrabDesk has no renamable selection.
    Func<bool>? CanRenameDesktopItems { get; set; }
    // Called by the low-level keyboard hook before a desktop command is consumed.
    // Returning false leaves the key with Explorer or the active application.
    Func<DesktopKeyboardCommand, bool>? CanHandleDesktopKeyboardCommand { get; set; }
    void TrackDesktopContextMenu();
}

public sealed class DesktopIconZoomEventArgs(int delta) : EventArgs
{
    public int Delta { get; } = delta;
    public int X { get; }
    public int Y { get; }

    public DesktopIconZoomEventArgs(int delta, int x, int y) : this(delta)
    {
        X = x;
        Y = y;
    }
}

public interface IUpdateService : IDisposable
{
    Task<UpdateCheckResult> CheckAsync(
        UpdateCheckRequest request,
        CancellationToken cancellationToken = default);

    Task<UpdateDownloadResult> DownloadAsync(
        UpdateDownloadRequest request,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IFileOperationService
{
    void Open(DesktopItemRef item);
    void OpenLocation(DesktopItemRef item);
    void ShowProperties(DesktopItemRef item);
    Task<string> RenameAsync(DesktopItemRef item, string newName, CancellationToken cancellationToken = default);
    Task DeleteAsync(IEnumerable<DesktopItemRef> items, CancellationToken cancellationToken = default);
    Task<FileImportBatchResult> ImportAsync(IEnumerable<string> sourcePaths, string destinationDirectory, bool move, CancellationToken cancellationToken = default);
    void SetClipboardFiles(IEnumerable<DesktopItemRef> items, bool move);
    FileClipboardContent GetClipboardFiles();
    void ClearClipboardFiles();
}

public interface ILayoutStore
{
    string StatePath { get; }
    Task<CrabDeskState> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CrabDeskState state, CancellationToken cancellationToken = default);
}

public interface IMonitorTopologyService
{
    IReadOnlyList<MonitorLayout> GetMonitors();
}

public interface IDesktopHost
{
    IntPtr DesktopParent { get; }
    IntPtr DesktopListView { get; }
    bool IsAvailable { get; }
    bool Refresh();
}

public interface IOrganizationRuleEngine
{
    IReadOnlyList<OrganizationDecision> Preview(
        CrabDeskState state,
        IReadOnlyList<DesktopItemRef> items,
        bool reassignExistingItems = false);

    OrganizationApplyResult Apply(
        CrabDeskState state,
        IReadOnlyList<DesktopItemRef> items,
        bool reassignExistingItems = false);

    IReadOnlyList<OrganizationRuleConflict> FindConflicts(CrabDeskState state);
}

public interface IBackupService
{
    string BackupDirectory { get; }
    Task<LayoutBackupInfo> CreateAsync(
        CrabDeskState state,
        DesktopBackupCapture? desktopCapture = null,
        CancellationToken cancellationToken = default);
    Task ExportAsync(
        CrabDeskState state,
        string destinationPath,
        DesktopBackupCapture? desktopCapture = null,
        CancellationToken cancellationToken = default);
    Task<CrabDeskState> LoadAsync(string path, CancellationToken cancellationToken = default);
    Task<LayoutBackupDocument> LoadDocumentAsync(string path, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LayoutBackupInfo>> GetBackupsAsync(CancellationToken cancellationToken = default);
    Task DeleteAsync(string path, CancellationToken cancellationToken = default);
    Task CleanupAsync(int retentionDays, CancellationToken cancellationToken = default);
}
