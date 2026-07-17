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

public interface IDesktopDoubleClickMonitor : IDisposable
{
    event EventHandler? EmptyAreaClicked;
    event EventHandler? EmptyAreaDoubleClicked;
    event EventHandler<DesktopIconZoomEventArgs>? IconZoomRequested;
    IntPtr DesktopListView { get; set; }
    bool Enabled { get; set; }
    bool DoubleClickEnabled { get; set; }
}

public sealed class DesktopIconZoomEventArgs(int delta) : EventArgs
{
    public int Delta { get; } = delta;
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

public interface IExplorerIconVisibility
{
    bool GetIconsHidden();
    void SetIconsHidden(bool hidden);
}

public interface IFileOperationService
{
    void Open(DesktopItemRef item);
    void OpenLocation(DesktopItemRef item);
    void ShowProperties(DesktopItemRef item);
    Task<string> RenameAsync(DesktopItemRef item, string newName, CancellationToken cancellationToken = default);
    Task DeleteAsync(IEnumerable<DesktopItemRef> items, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ImportAsync(IEnumerable<string> sourcePaths, string destinationDirectory, bool move, CancellationToken cancellationToken = default);
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
    Task<LayoutBackupInfo> CreateAsync(CrabDeskState state, CancellationToken cancellationToken = default);
    Task ExportAsync(CrabDeskState state, string destinationPath, CancellationToken cancellationToken = default);
    Task<CrabDeskState> LoadAsync(string path, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LayoutBackupInfo>> GetBackupsAsync(CancellationToken cancellationToken = default);
    Task DeleteAsync(string path, CancellationToken cancellationToken = default);
    Task CleanupAsync(int retentionDays, CancellationToken cancellationToken = default);
}
