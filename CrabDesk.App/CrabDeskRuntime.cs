using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CrabDesk.Core;
using CrabDesk.Native;

namespace CrabDesk.App;

public sealed class CrabDeskRuntime : IDisposable
{
    private static readonly SmartBoxSpec[] SmartBoxSpecs =
    [
        new("快捷方式", "#FF4FAED2", 10, [DesktopItemKind.Shortcut], []),
        new("目录", "#FF62B985", 20, [DesktopItemKind.Folder], []),
        new("文档", "#FFFFB454", 30, [DesktopItemKind.File],
            ["doc", "docx", "pdf", "rtf", "txt", "xls", "xlsx", "ppt", "pptx", "md"]),
        new("图片", "#FFFF756B", 40, [DesktopItemKind.File],
            ["bmp", "gif", "jpg", "jpeg", "png", "tif", "tiff", "webp", "heic"]),
        new("压缩包", "#FF9C82E5", 50, [DesktopItemKind.File],
            ["7z", "bz2", "gz", "rar", "tar", "xz", "zip"])
    ];
    private readonly Dispatcher _dispatcher;
    private readonly ILayoutStore _layoutStore = new JsonLayoutStore();
    private readonly IMonitorTopologyService _monitorService = new MonitorTopologyService();
    private readonly DesktopHostService _desktopHost = new();
    private readonly IExplorerIconVisibility _iconVisibility = new ExplorerIconVisibility();
    private readonly IDesktopItemProvider _itemProvider = new DesktopItemProvider();
    private readonly IMappedFolderProvider _mappedFolderProvider = new MappedFolderProvider();
    private readonly IFileOperationService _fileOperations = new FileOperationService();
    private readonly IHotkeyService _hotkeyService = new GlobalHotkeyService();
    private readonly IDesktopContextMenuRegistration _desktopContextMenu = new DesktopContextMenuRegistration();
    private IDesktopDoubleClickMonitor? _desktopDoubleClickMonitor;
    private readonly IOrganizationRuleEngine _organizationRuleEngine = new OrganizationRuleEngine();
    private readonly IUpdateService _updateService = new GitHubUpdateService();
    private readonly ShellIconProvider _iconProvider = new();
    private readonly DispatcherTimer _hostTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly SemaphoreSlim _mappedRefreshLock = new(1, 1);
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly CancellationTokenSource _updateCancellation = new();
    private readonly Dictionary<Guid, MappedFolderSnapshot> _mappedFolderSnapshots = [];
    private readonly Dictionary<HotkeyAction, HotkeyRegistrationStatus> _hotkeyStatuses = [];
    private readonly Dictionary<string, DesktopIconPositionSnapshot> _originalIconPositions =
        new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<DesktopItemRef> _allDesktopItems = [];
    private Dictionary<string, Guid>? _lastOrganizationAssignments;
    private DesktopSurfaceManager? _surfaceManager;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Windows.Forms.ContextMenuStrip? _trayMenu;
    private System.Windows.Forms.ToolStripMenuItem? _pauseTrayItem;
    private System.Windows.Forms.ToolStripMenuItem? _startupTrayItem;
    private readonly Dictionary<ApplicationThemeMode, System.Windows.Forms.ToolStripMenuItem> _themeTrayItems = [];
    private readonly System.Windows.Forms.ToolStripProfessionalRenderer _lightTrayRenderer =
        new(new ThemedTrayColorTable(false));
    private readonly System.Windows.Forms.ToolStripProfessionalRenderer _darkTrayRenderer =
        new(new ThemedTrayColorTable(true));
    private System.Drawing.Icon? _applicationIcon;
    private bool _trayHintShown;
    private bool _originalIconsHidden;
    private bool _originalIconStateCaptured;
    private string? _recoveryMarker;
    private bool _guardStarted;
    private bool _disposed;
    private bool _hostCheckInProgress;
    private DateTimeOffset _lastMappedHealthCheckAt;

    public CrabDeskRuntime(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _hostTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, OnHostTimer, dispatcher);
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, OnSaveTimer, dispatcher)
        {
            IsEnabled = false
        };
        _itemProvider.ItemsChanged += (_, _) => _dispatcher.BeginInvoke(OnDesktopItemsChanged);
        _mappedFolderProvider.ItemsChanged += (_, _) => _dispatcher.BeginInvoke(() => RefreshMappedFoldersAsync());
        _hotkeyService.Pressed += OnGlobalHotkeyPressed;
    }

    public event EventHandler? Changed;
    public event EventHandler? ShowSettingsRequested;
    public event EventHandler? ExitRequested;

    public CrabDeskState State { get; private set; } = new();
    public IReadOnlyList<DesktopItemRef> Items { get; private set; } = [];
    public IReadOnlyList<MonitorLayout> Monitors { get; private set; } = [];
    public bool IsPaused { get; private set; }
    public bool IsDarkTheme { get; private set; }
    public bool AreDesktopItemsHidden { get; private set; }
    public bool IsCheckingForUpdates { get; private set; }
    public bool DesktopConnected => _desktopHost.IsAvailable && !IsPaused;
    public bool CanUndoOrganization => _lastOrganizationAssignments is not null;
    public IFileOperationService FileOperations => _fileOperations;
    public ShellIconProvider IconProvider => _iconProvider;
    public string CurrentVersion => UpdateConfiguration.CurrentVersion;
    public string ConfigDirectory => Path.GetDirectoryName(_layoutStore.StatePath)!;
    public UpdateCheckResult LastUpdateCheck { get; private set; } = new(
        UpdateCheckStatus.NotChecked,
        UpdateConfiguration.CurrentVersion);
    public HotkeyRegistrationStatus GetHotkeyStatus(HotkeyAction action) =>
        _hotkeyStatuses.GetValueOrDefault(action, HotkeyRegistrationStatus.Disabled);

    public DesktopHostDiagnostics GetDesktopHostDiagnostics()
    {
        var parent = _desktopHost.DesktopParent;
        var view = _desktopHost.DesktopView;
        var listView = _desktopHost.DesktopListView;
        return new DesktopHostDiagnostics(
            DateTimeOffset.Now,
            DesktopConnected,
            IsPaused,
            FormatHandle(parent),
            DesktopHostService.GetWindowClass(parent),
            FormatHandle(view),
            DesktopHostService.GetWindowClass(view),
            FormatHandle(listView),
            Monitors.Count,
            _surfaceManager?.SurfaceCount ?? 0,
            State.Boxes.Count,
            State.Boxes.Count(box => box.IsMappedFolder),
            State.Assignments.Count,
            State.SchemaVersion,
            $"{State.Settings.ThemeMode} / {(IsDarkTheme ? "Dark" : "Light")}",
            Monitors.Select(monitor =>
                $"{monitor.DeviceName} {(monitor.IsPrimary ? "Primary" : "Secondary")} " +
                $"{monitor.PixelBounds.Width:0}x{monitor.PixelBounds.Height:0} " +
                $"@ {monitor.DpiScale * 100:0}% ({monitor.PixelBounds.X:0},{monitor.PixelBounds.Y:0})")
                .ToArray());
    }

    public string GetDesktopHostDiagnosticsText()
    {
        var diagnostics = GetDesktopHostDiagnostics();
        return string.Join(Environment.NewLine,
            "CrabDesk desktop diagnostics",
            $"Captured: {diagnostics.CapturedAt:O}",
            $"Version: {CurrentVersion}",
            $"OS: {Environment.OSVersion.VersionString}",
            $"Connected: {diagnostics.Connected}",
            $"Paused: {diagnostics.Paused}",
            $"DesktopParent: {diagnostics.DesktopParentHandle} [{diagnostics.DesktopParentClass}]",
            $"DesktopView: {diagnostics.DesktopViewHandle} [{diagnostics.DesktopViewClass}]",
            $"DesktopListView: {diagnostics.DesktopListViewHandle}",
            $"Monitors/Surfaces: {diagnostics.MonitorCount}/{diagnostics.SurfaceCount}",
            $"Boxes/Mapped/Assignments: {diagnostics.BoxCount}/{diagnostics.MappedBoxCount}/{diagnostics.AssignmentCount}",
            $"Schema: {diagnostics.SchemaVersion}",
            $"Theme: {diagnostics.Theme}",
            "Topology:",
            string.Join(Environment.NewLine, diagnostics.Monitors.Select(monitor => "  " + monitor)));
    }

    public void OpenConfigDirectory()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Process.Start(new ProcessStartInfo(ConfigDirectory) { UseShellExecute = true });
    }

    public int ClearThumbnailCache()
    {
        var cleared = _iconProvider.ClearCache();
        cleared += _surfaceManager?.ClearIconCaches() ?? 0;
        _surfaceManager?.Refresh();
        return cleared;
    }

    internal bool TrayThemeMatchesCurrentTheme()
    {
        if (_trayMenu is null)
        {
            return false;
        }
        var expectedBackground = IsDarkTheme
            ? System.Drawing.Color.FromArgb(37, 40, 45)
            : System.Drawing.Color.White;
        var expectedForeground = IsDarkTheme
            ? System.Drawing.Color.FromArgb(244, 245, 247)
            : System.Drawing.Color.FromArgb(32, 36, 42);
        var expectedRenderer = IsDarkTheme ? _darkTrayRenderer : _lightTrayRenderer;
        return _trayMenu.BackColor == expectedBackground &&
            _trayMenu.ForeColor == expectedForeground &&
            ReferenceEquals(_trayMenu.Renderer, expectedRenderer);
    }

    public async Task InitializeAsync()
    {
        State = await _layoutStore.LoadAsync();
        var updateRepository = UpdateConfiguration.ResolveRepository(State.Settings.Updates);
        if (!string.IsNullOrWhiteSpace(updateRepository.Owner) &&
            !string.IsNullOrWhiteSpace(updateRepository.Repository))
        {
            State.Settings.Updates.RepositoryOwner = updateRepository.Owner;
            State.Settings.Updates.RepositoryName = updateRepository.Repository;
        }
        var cachedUpdate = State.Settings.Updates;
        var cachedStatus = cachedUpdate.LastStatus;
        if (SemanticVersion.TryParse(CurrentVersion, out var currentSemanticVersion) &&
            SemanticVersion.TryParse(cachedUpdate.LatestKnownVersion, out var cachedSemanticVersion) &&
            cachedStatus is UpdateCheckStatus.UpToDate or UpdateCheckStatus.UpdateAvailable)
        {
            cachedStatus = cachedSemanticVersion.CompareTo(currentSemanticVersion) > 0
                ? UpdateCheckStatus.UpdateAvailable
                : UpdateCheckStatus.UpToDate;
        }
        LastUpdateCheck = new UpdateCheckResult(
            cachedStatus,
            CurrentVersion,
            cachedUpdate.LatestKnownVersion,
            cachedUpdate.CachedReleaseName,
            cachedUpdate.CachedPublishedAt,
            cachedUpdate.CachedReleaseNotes,
            cachedUpdate.CachedReleasePageUrl,
            cachedUpdate.CachedInstallerUrl,
            cachedUpdate.CachedSha256Url,
            cachedUpdate.CachedIsPrerelease,
            cachedUpdate.CachedETag,
            cachedUpdate.LastMessage);
        State.Settings.StartWithWindows = StartupRegistration.IsEnabled();
        ApplyHotkeys();
        try
        {
            _desktopContextMenu.SetEnabled(
                State.Settings.DesktopBehavior.ShowDesktopContextMenu,
                Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CrabDesk.App.exe"));
        }
        catch
        {
            State.Settings.DesktopBehavior.ShowDesktopContextMenu = false;
        }
        ApplyTheme(false);
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _desktopHost.Refresh();
        ConfigureDesktopDoubleClickMonitor(State.Settings.DesktopBehavior.ToggleIconsOnDesktopDoubleClick);
        RecoverStaleIconState();
        Monitors = _monitorService.GetMonitors();
        NormalizeMonitorIds();
        LayoutCoordinator.NormalizeForMonitors(State, Monitors);
        await RefreshItemsAsync(false);
        await RunDailyBackupIfNeededAsync();
        if (State.Organization.Enabled && State.Organization.RunOnStartup)
        {
            ApplyOrganizationRules();
        }

        if (State.Settings.TakeOverDesktop)
        {
            StartTakeover();
        }
        else
        {
            IsPaused = true;
        }

        CreateTrayIcon();
        _hostTimer.Start();
        ScheduleSave();
        if (State.Settings.Updates.CheckOnStartup)
        {
            _ = CheckForUpdatesAsync(false);
        }
    }

    public IReadOnlyList<DesktopItemRef> GetItemsForBox(Guid boxId)
    {
        var box = State.Boxes.First(candidate => candidate.Id == boxId);
        IEnumerable<DesktopItemRef> query = box.IsMappedFolder
            ? _mappedFolderSnapshots.GetValueOrDefault(boxId)?.Items ?? []
            : Items.Where(item =>
                State.Assignments.TryGetValue(item.Key.ToString(), out var assignedBox) && assignedBox == boxId);
        query = box.SortMode switch
        {
            BoxSortMode.Name => query.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            BoxSortMode.Type => query.OrderBy(item => item.Kind).ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            BoxSortMode.Modified => query.OrderByDescending(item => item.ModifiedAt).ThenBy(item => item.DisplayName),
            _ => query.OrderBy(item =>
            {
                var index = box.ItemOrder.IndexOf(item.Key.ToString());
                return index < 0 ? int.MaxValue : index;
            })
        };
        return query.ToArray();
    }

    internal IReadOnlyList<DesktopItemRef> GetUnassignedDesktopItems() => _allDesktopItems
        .Where(item => !State.Assignments.ContainsKey(item.Key.ToString()))
        .ToArray();

    internal bool TryGetDesktopIconPosition(DesktopItemRef item, out DesktopIconPositionSnapshot position) =>
        _originalIconPositions.TryGetValue(item.Key.ToString(), out position);

    public bool ReorderBoxItems(Guid boxId, IReadOnlyCollection<string> movingKeys, string? beforeKey)
    {
        var box = State.Boxes.First(candidate => candidate.Id == boxId);
        var currentKeys = GetItemsForBox(boxId).Select(item => item.Key.ToString()).ToArray();
        if (!LayoutCoordinator.ReorderItems(box, currentKeys, movingKeys, beforeKey))
        {
            return false;
        }
        NotifyWorkspaceChanged(true);
        return true;
    }

    public MappedFolderSnapshot? GetMappedFolderSnapshot(Guid boxId) =>
        _mappedFolderSnapshots.GetValueOrDefault(boxId);

    public DesktopBox AddBox(string title = "新盒子")
    {
        var monitor = Monitors.FirstOrDefault(candidate => candidate.IsPrimary) ?? Monitors.First();
        var box = new DesktopBox
        {
            Title = title,
            MonitorId = monitor.Id,
            Bounds = FindAvailableBoxBounds(monitor, 420, 310)
        };
        State.Boxes.Add(box);
        NotifyWorkspaceChanged(true);
        return box;
    }

    public async Task<DesktopBox> AddMappedFolderBoxAsync(string path, bool isReadOnly = false)
    {
        var normalizedPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        var monitor = Monitors.FirstOrDefault(candidate => candidate.IsPrimary) ?? Monitors.First();
        var box = new DesktopBox
        {
            Title = Path.GetFileName(normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            MonitorId = monitor.Id,
            Bounds = FindAvailableBoxBounds(monitor, 420, 310),
            MappedFolder = new MappedFolderSettings
            {
                Path = normalizedPath,
                IsReadOnly = isReadOnly
            }
        };
        if (string.IsNullOrWhiteSpace(box.Title))
        {
            box.Title = normalizedPath;
        }
        State.Boxes.Add(box);
        await RefreshMappedFoldersAsync(false);
        NotifyWorkspaceChanged(true);
        return box;
    }

    public async Task UpdateMappedFolderAsync(DesktopBox box, string path)
    {
        if (box.MappedFolder is null)
        {
            throw new InvalidOperationException("所选盒子不是映射文件夹。");
        }
        box.MappedFolder.Path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        await RefreshMappedFoldersAsync(false);
        NotifyWorkspaceChanged(true);
    }

    public void SetMappedFolderReadOnly(DesktopBox box, bool isReadOnly)
    {
        if (box.MappedFolder is null)
        {
            return;
        }
        box.MappedFolder.IsReadOnly = isReadOnly;
        NotifyWorkspaceChanged(true);
    }

    public void DeleteBox(DesktopBox box)
    {
        if (!box.IsMappedFolder && State.Boxes.Count(candidate => !candidate.IsMappedFolder) <= 1)
        {
            return;
        }

        foreach (var key in State.Assignments.Where(pair => pair.Value == box.Id).Select(pair => pair.Key).ToArray())
        {
            UnassignItemCore(key);
        }
        State.Boxes.Remove(box);
        _mappedFolderSnapshots.Remove(box.Id);
        ConfigureMappedFolderWatchers();
        NotifyWorkspaceChanged(true);
    }

    public void AssignItem(string itemKey, Guid boxId)
    {
        if (State.Boxes.FirstOrDefault(box => box.Id == boxId)?.IsMappedFolder != false)
        {
            return;
        }
        var item = Items.FirstOrDefault(candidate => candidate.Key.ToString() == itemKey);
        if (item is null)
        {
            return;
        }
        if (item is not null)
        {
            CaptureOriginalIconPositions([item]);
        }
        State.Assignments[itemKey] = boxId;
        MoveItemOrderKey(itemKey, boxId);
        NotifyWorkspaceChanged(true);
    }

    public void UnassignItem(string itemKey)
    {
        UnassignItemCore(itemKey);
        NotifyWorkspaceChanged(true);
    }

    public void BoxChanged(DesktopBox box, bool rebuild = false)
    {
        var monitor = Monitors.FirstOrDefault(candidate => candidate.Id == box.MonitorId)
            ?? Monitors.FirstOrDefault(candidate => candidate.IsPrimary)
            ?? Monitors.First();
        box.Bounds = box.Bounds.Clamp(new LayoutRect(0, 0, monitor.WorkArea.Width, monitor.WorkArea.Height));
        NotifyWorkspaceChanged(rebuild);
    }

    public async Task ImportFilesAsync(IEnumerable<string> paths, Guid boxId, bool move)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var imported = await _fileOperations.ImportAsync(paths, desktop, move);
        await RefreshItemsAsync();
        var importedSet = imported.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items.Where(item => item.FileSystemPath is not null && importedSet.Contains(Path.GetFullPath(item.FileSystemPath))))
        {
            CaptureOriginalIconPositions([item]);
            State.Assignments[item.Key.ToString()] = boxId;
            MoveItemOrderKey(item.Key.ToString(), boxId);
        }
        NotifyWorkspaceChanged(true);
    }

    public async Task ImportFilesToBoxAsync(IEnumerable<string> paths, Guid boxId, bool move)
    {
        var box = State.Boxes.First(candidate => candidate.Id == boxId);
        if (!box.IsMappedFolder)
        {
            await ImportFilesAsync(paths, boxId, move);
            return;
        }
        if (box.MappedFolder!.IsReadOnly)
        {
            throw new InvalidOperationException("此映射盒子已设为只读。");
        }
        var snapshot = GetMappedFolderSnapshot(boxId);
        if (snapshot?.IsAvailable != true)
        {
            throw new DirectoryNotFoundException(snapshot?.Message ?? "映射文件夹不可用。");
        }
        await _fileOperations.ImportAsync(paths, box.MappedFolder.Path, move);
        await RefreshMappedFoldersAsync();
    }

    public async Task TransferBoxItemsAsync(
        Guid sourceBoxId,
        IEnumerable<string> itemKeys,
        Guid targetBoxId,
        bool move)
    {
        if (sourceBoxId == targetBoxId)
        {
            return;
        }
        var source = State.Boxes.First(candidate => candidate.Id == sourceBoxId);
        var target = State.Boxes.First(candidate => candidate.Id == targetBoxId);
        var keys = itemKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = GetItemsForBox(sourceBoxId).Where(item => keys.Contains(item.Key.ToString())).ToArray();
        if (items.Length == 0)
        {
            return;
        }

        if (!target.IsMappedFolder && !source.IsMappedFolder)
        {
            foreach (var item in items)
            {
                AssignItem(item.Key.ToString(), targetBoxId);
            }
            return;
        }

        var paths = items.Select(item => item.FileSystemPath).OfType<string>().ToArray();
        if (paths.Length == 0)
        {
            return;
        }
        await ImportFilesToBoxAsync(paths, targetBoxId, move);
        if (move && !source.IsMappedFolder)
        {
            foreach (var item in items)
            {
                UnassignItemCore(item.Key.ToString());
            }
            await RefreshItemsAsync(false);
        }
        if (source.IsMappedFolder)
        {
            await RefreshMappedFoldersAsync();
        }
    }

    public bool CanPasteIntoBox(DesktopBox box)
    {
        if (box.MappedFolder?.IsReadOnly == true)
        {
            return false;
        }
        try
        {
            return _fileOperations.GetClipboardFiles().HasFiles;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return false;
        }
    }

    public async Task<int> PasteIntoBoxAsync(Guid boxId)
    {
        var box = State.Boxes.First(candidate => candidate.Id == boxId);
        if (box.MappedFolder?.IsReadOnly == true)
        {
            throw new InvalidOperationException("此映射盒子已设为只读。");
        }
        var clipboard = _fileOperations.GetClipboardFiles();
        if (!clipboard.HasFiles)
        {
            return 0;
        }

        if (box.IsMappedFolder)
        {
            await ImportFilesToBoxAsync(clipboard.Paths, boxId, clipboard.Move);
            if (clipboard.Move)
            {
                _fileOperations.ClearClipboardFiles();
            }
            return clipboard.Paths.Count;
        }

        var desktopItems = Items
            .Where(item => item.FileSystemPath is not null)
            .ToDictionary(item => Path.GetFullPath(item.FileSystemPath!), StringComparer.OrdinalIgnoreCase);
        var external = new List<string>();
        var assigned = 0;
        foreach (var path in clipboard.Paths)
        {
            var fullPath = Path.GetFullPath(path);
            if (desktopItems.TryGetValue(fullPath, out var item))
            {
                AssignItem(item.Key.ToString(), boxId);
                assigned++;
            }
            else if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                external.Add(fullPath);
            }
        }
        if (external.Count > 0)
        {
            await ImportFilesAsync(external, boxId, clipboard.Move);
            assigned += external.Count;
        }
        if (clipboard.Move && assigned > 0)
        {
            _fileOperations.ClearClipboardFiles();
        }
        return assigned;
    }

    public async Task RenameItemAsync(DesktopItemRef item, string newName, Guid boxId)
    {
        var oldKey = item.Key.ToString();
        var destination = await _fileOperations.RenameAsync(item, newName);
        if (State.Boxes.FirstOrDefault(box => box.Id == boxId)?.IsMappedFolder == true)
        {
            await RefreshMappedFoldersAsync();
            var renamedMapped = GetItemsForBox(boxId).FirstOrDefault(candidate =>
                candidate.FileSystemPath is not null &&
                string.Equals(Path.GetFullPath(candidate.FileSystemPath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase));
            if (renamedMapped is not null)
            {
                ReplaceItemOrderKey(oldKey, renamedMapped.Key.ToString());
                NotifyWorkspaceChanged(true);
            }
            return;
        }

        await RefreshItemsAsync(false);
        var renamed = Items.FirstOrDefault(candidate => candidate.FileSystemPath is not null &&
            string.Equals(Path.GetFullPath(candidate.FileSystemPath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase));
        if (renamed is null)
        {
            return;
        }
        var newKey = renamed.Key.ToString();
        State.Assignments.Remove(oldKey);
        State.Assignments[newKey] = boxId;
        ReplaceItemOrderKey(oldKey, newKey);
        NotifyWorkspaceChanged(true);
    }

    internal async Task RenameUnassignedItemAsync(DesktopItemRef item, string newName)
    {
        await _fileOperations.RenameAsync(item, newName);
        await RefreshItemsAsync(false);
    }

    public async Task RefreshItemsAsync(bool applyDesktopRules = true)
    {
        var items = await _itemProvider.EnumerateAsync();
        _allDesktopItems = items;
        Items = State.Settings.ShowSystemItems
            ? items
            : items.Where(item => !item.IsSystem || State.Assignments.ContainsKey(item.Key.ToString())).ToArray();
        CaptureOriginalIconPositions(_allDesktopItems);
        await RefreshMappedFoldersAsync(false);
        if (applyDesktopRules && State.Organization.Enabled && State.Organization.RunOnDesktopChanges)
        {
            ApplyOrganizationRules(false);
        }
        _surfaceManager?.Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public void SetPaused(bool paused)
    {
        if (paused == IsPaused)
        {
            return;
        }

        IsPaused = paused;
        State.Settings.TakeOverDesktop = !paused;
        if (paused)
        {
            AreDesktopItemsHidden = false;
            _surfaceManager?.Dispose();
            _surfaceManager = null;
            RestoreOriginalIconPositions(true);
            _iconVisibility.SetIconsHidden(_originalIconsHidden);
        }
        else
        {
            if (!_originalIconStateCaptured)
            {
                _originalIconsHidden = State.MigratedFromLegacyIconTakeover
                    ? false
                    : _iconVisibility.GetIconsHidden();
                _originalIconStateCaptured = true;
            }
            EnsureRecoveryGuard();
            AreDesktopItemsHidden = false;
            CaptureOriginalIconPositions(_allDesktopItems);
            _iconVisibility.SetIconsHidden(true);
            RebuildSurfaces();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public void SetStartWithWindows(bool enabled)
    {
        StartupRegistration.SetEnabled(enabled);
        State.Settings.StartWithWindows = enabled;
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public async Task SetShowSystemItemsAsync(bool enabled)
    {
        State.Settings.ShowSystemItems = enabled;
        await RefreshItemsAsync();
    }

    public void SetConfirmDeleteBox(bool enabled)
    {
        State.Settings.ConfirmDeleteBox = enabled;
        ScheduleSave();
    }

    public void SetLaunchToTray(bool enabled)
    {
        State.Settings.DesktopBehavior.LaunchToTray = enabled;
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public void SetHotkey(
        HotkeyAction action,
        bool enabled,
        HotkeyModifiers modifiers,
        HotkeyKey key)
    {
        var binding = GetHotkeyBinding(action);
        binding.Enabled = enabled;
        binding.Modifiers = modifiers;
        binding.Key = key;
        ApplyHotkeys();
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public void SetDesktopContextMenuEnabled(bool enabled)
    {
        _desktopContextMenu.SetEnabled(
            enabled,
            Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CrabDesk.App.exe"));
        State.Settings.DesktopBehavior.ShowDesktopContextMenu = _desktopContextMenu.IsEnabled;
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public void SetToggleIconsOnDesktopDoubleClick(bool enabled)
    {
        ConfigureDesktopDoubleClickMonitor(enabled);
        State.Settings.DesktopBehavior.ToggleIconsOnDesktopDoubleClick = enabled;
        if (!enabled && AreDesktopItemsHidden)
        {
            AreDesktopItemsHidden = false;
            _iconVisibility.SetIconsHidden(IsPaused ? _originalIconsHidden : true);
            _surfaceManager?.Refresh();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public void SetCheckUpdatesOnStartup(bool enabled)
    {
        State.Settings.Updates.CheckOnStartup = enabled;
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public void SetUpdateChannel(UpdateChannel channel)
    {
        if (State.Settings.Updates.Channel == channel)
        {
            return;
        }
        State.Settings.Updates.Channel = channel;
        State.Settings.Updates.CachedETag = string.Empty;
        State.Settings.Updates.LatestKnownVersion = string.Empty;
        State.Settings.Updates.CachedReleaseName = string.Empty;
        State.Settings.Updates.CachedPublishedAt = null;
        State.Settings.Updates.CachedReleaseNotes = string.Empty;
        State.Settings.Updates.CachedReleasePageUrl = string.Empty;
        State.Settings.Updates.CachedInstallerUrl = string.Empty;
        State.Settings.Updates.CachedSha256Url = string.Empty;
        State.Settings.Updates.CachedIsPrerelease = false;
        State.Settings.Updates.LastCheckedAt = null;
        State.Settings.Updates.LastStatus = UpdateCheckStatus.NotChecked;
        State.Settings.Updates.LastMessage = string.Empty;
        LastUpdateCheck = new UpdateCheckResult(UpdateCheckStatus.NotChecked, CurrentVersion);
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(bool manual = true)
    {
        if (!await _updateLock.WaitAsync(0))
        {
            return LastUpdateCheck;
        }
        IsCheckingForUpdates = true;
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            var settings = State.Settings.Updates;
            var repository = UpdateConfiguration.ResolveRepository(settings);
            var request = new UpdateCheckRequest(
                repository.Owner,
                repository.Repository,
                CurrentVersion,
                settings.Channel,
                settings.CachedETag,
                settings.LatestKnownVersion,
                settings.CachedReleaseName,
                settings.CachedPublishedAt,
                settings.CachedReleaseNotes,
                settings.CachedReleasePageUrl,
                settings.CachedInstallerUrl,
                settings.CachedSha256Url,
                settings.CachedIsPrerelease);
            var result = await _updateService.CheckAsync(request, _updateCancellation.Token);
            LastUpdateCheck = result;
            settings.LastStatus = result.Status;
            settings.LastMessage = result.Message;
            if (result.Status != UpdateCheckStatus.NotConfigured)
            {
                settings.LastCheckedAt = DateTimeOffset.Now;
            }
            if (!string.IsNullOrWhiteSpace(result.LatestVersion))
            {
                settings.CachedETag = result.ETag;
                settings.LatestKnownVersion = result.LatestVersion;
                settings.CachedReleaseName = result.ReleaseName;
                settings.CachedPublishedAt = result.PublishedAt;
                settings.CachedReleaseNotes = result.ReleaseNotes;
                settings.CachedReleasePageUrl = result.ReleasePageUrl;
                settings.CachedInstallerUrl = result.InstallerUrl;
                settings.CachedSha256Url = result.Sha256Url;
                settings.CachedIsPrerelease = result.IsPrerelease;
            }
            ScheduleSave();
            if (!manual && result.Status == UpdateCheckStatus.UpdateAvailable)
            {
                _trayIcon?.ShowBalloonTip(
                    3500,
                    "CrabDesk 有新版本",
                    $"{result.LatestVersion} 已发布，可在设置中查看。",
                    System.Windows.Forms.ToolTipIcon.Info);
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            return LastUpdateCheck;
        }
        finally
        {
            IsCheckingForUpdates = false;
            _updateLock.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void OpenLatestReleasePage()
    {
        var url = LastUpdateCheck.ReleasePageUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            url = State.Settings.Updates.CachedReleasePageUrl;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var releaseUri) ||
            releaseUri.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }
        Process.Start(new ProcessStartInfo(releaseUri.AbsoluteUri) { UseShellExecute = true });
    }

    public void OpenLocalDocument(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, Path.GetFileName(fileName));
        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }

    public void SetCornerRadius(double value)
    {
        State.Settings.Appearance.CornerRadius = Math.Clamp(value, 0, 20);
        NotifyWorkspaceChanged(true);
    }

    public void SetShowBoxBorder(bool enabled)
    {
        State.Settings.Appearance.ShowBorder = enabled;
        NotifyWorkspaceChanged(true);
    }

    public void SetShowResizeGrip(bool enabled)
    {
        State.Settings.Appearance.ShowResizeGrip = enabled;
        NotifyWorkspaceChanged(true);
    }

    public void SetHoverFeedback(bool enabled)
    {
        State.Settings.Appearance.HoverFeedback = enabled;
        NotifyWorkspaceChanged(true);
    }

    public void SetExpandBoxOnHover(bool enabled)
    {
        State.Settings.DesktopBehavior.ExpandBoxOnHover = enabled;
        NotifyWorkspaceChanged(true);
    }

    public void SetRefreshAfterRename(bool enabled)
    {
        State.Settings.DesktopBehavior.RefreshAfterRename = enabled;
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public void SetAnimationEnabled(bool enabled)
    {
        State.Settings.Appearance.AnimationEnabled = enabled;
        NotifyWorkspaceChanged(true);
    }

    public void SetIconSpacing(double horizontal, double vertical)
    {
        State.Settings.Appearance.IconHorizontalSpacing = Math.Clamp(horizontal, 56, 160);
        State.Settings.Appearance.IconVerticalSpacing = Math.Clamp(vertical, 56, 180);
        NotifyWorkspaceChanged(true);
    }

    public void SetSelectionColor(string value)
    {
        State.Settings.Appearance.SelectionColor = value;
        NotifyWorkspaceChanged(true);
    }

    public void SetBoxTitleAlignment(Guid? boxId, BoxTitleAlignment alignment)
    {
        foreach (var box in GetAppearanceTargets(boxId))
        {
            box.Appearance.TitleAlignment = alignment;
        }
        NotifyWorkspaceChanged(true);
    }

    public void SetBoxBackground(Guid? boxId, string value)
    {
        foreach (var box in GetAppearanceTargets(boxId))
        {
            box.Appearance.Background = value;
        }
        NotifyWorkspaceChanged(true);
    }

    public void SetBoxAccent(Guid? boxId, string value)
    {
        foreach (var box in GetAppearanceTargets(boxId))
        {
            box.Appearance.Accent = value;
        }
        NotifyWorkspaceChanged(true);
    }

    public void SetBoxOpacity(Guid? boxId, double value)
    {
        foreach (var box in GetAppearanceTargets(boxId))
        {
            box.Appearance.Opacity = Math.Clamp(value, 0.35, 1);
        }
        NotifyWorkspaceChanged(true);
    }

    public void SetBoxTitleBarHeight(Guid? boxId, double value)
    {
        foreach (var box in GetAppearanceTargets(boxId))
        {
            box.Appearance.TitleBarHeight = Math.Clamp(value, 32, 56);
        }
        NotifyWorkspaceChanged(true);
    }

    public void SetBoxTitleColor(Guid? boxId, string value)
    {
        foreach (var box in GetAppearanceTargets(boxId))
        {
            box.Appearance.TitleColor = value;
        }
        NotifyWorkspaceChanged(true);
    }

    public void SetBoxTitleFontSize(Guid? boxId, double value)
    {
        foreach (var box in GetAppearanceTargets(boxId))
        {
            box.Appearance.TitleFontSize = Math.Clamp(value, 8, 20);
        }
        NotifyWorkspaceChanged(true);
    }

    public void SetBoxTitleFontBold(Guid? boxId, bool enabled)
    {
        foreach (var box in GetAppearanceTargets(boxId))
        {
            box.Appearance.TitleFontBold = enabled;
        }
        NotifyWorkspaceChanged(true);
    }

    public void SetShowCollapseButton(Guid? boxId, bool enabled)
    {
        foreach (var box in GetAppearanceTargets(boxId))
        {
            box.Appearance.ShowCollapseButton = enabled;
        }
        NotifyWorkspaceChanged(true);
    }

    public void SetOrganizationEnabled(bool enabled)
    {
        State.Organization.Enabled = enabled;
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public void SetRunRulesOnStartup(bool enabled)
    {
        State.Organization.RunOnStartup = enabled;
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public void SetRunRulesOnDesktopChanges(bool enabled)
    {
        State.Organization.RunOnDesktopChanges = enabled;
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public void SetReassignExistingItems(bool enabled)
    {
        State.Organization.ReassignExistingItems = enabled;
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public string BackupDirectory => GetBackupService().BackupDirectory;

    public async Task<LayoutBackupInfo> CreateBackupAsync()
    {
        await SaveNowAsync();
        var service = GetBackupService();
        var backup = await service.CreateAsync(State);
        State.Settings.Backup.LastBackupAt = DateTimeOffset.Now;
        await service.CleanupAsync(State.Settings.Backup.RetentionDays);
        ScheduleSave();
        Changed?.Invoke(this, EventArgs.Empty);
        return backup;
    }

    public async Task<LayoutResetResult> ResetLayoutAsync()
    {
        var backup = await CreateBackupAsync();
        RestoreOriginalIconPositions(true);
        CaptureOriginalIconPositions(_allDesktopItems);
        var primary = Monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? Monitors.FirstOrDefault();
        var disabledRules = LayoutCoordinator.ResetLayout(State, primary?.Id ?? "primary");
        _lastOrganizationAssignments = null;
        _mappedFolderSnapshots.Clear();
        ConfigureMappedFolderWatchers();
        NormalizeMonitorIds();
        LayoutCoordinator.NormalizeForMonitors(State, Monitors);
        NotifyWorkspaceChanged(true);
        await SaveNowAsync();
        return new LayoutResetResult(backup, disabledRules);
    }

    public Task<IReadOnlyList<LayoutBackupInfo>> GetBackupsAsync() =>
        GetBackupService().GetBackupsAsync();

    public Task ExportBackupAsync(string path) => GetBackupService().ExportAsync(State, path);

    public async Task RestoreBackupAsync(string path)
    {
        var service = GetBackupService();
        await service.CreateAsync(State);
        var imported = await service.LoadAsync(path);
        var previous = State;
        try
        {
            await ApplyLoadedStateAsync(imported);
            await SaveNowAsync();
        }
        catch
        {
            await ApplyLoadedStateAsync(previous);
            throw;
        }
    }

    public async Task DeleteBackupAsync(string path)
    {
        await GetBackupService().DeleteAsync(path);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetDailyBackup(bool enabled)
    {
        State.Settings.Backup.DailyBackup = enabled;
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public async Task SetBackupRetentionDaysAsync(int days)
    {
        State.Settings.Backup.RetentionDays = Math.Clamp(days, 1, 365);
        await GetBackupService().CleanupAsync(State.Settings.Backup.RetentionDays);
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public OrganizationApplyResult ApplyOrganizationRules(bool notify = true)
    {
        var decisions = _organizationRuleEngine.Preview(
            State,
            Items,
            State.Organization.ReassignExistingItems);
        var validBoxes = State.Boxes.Where(box => !box.IsMappedFolder).Select(box => box.Id).ToHashSet();
        if (decisions.Count > 0)
        {
            _lastOrganizationAssignments = new Dictionary<string, Guid>(
                State.Assignments,
                StringComparer.OrdinalIgnoreCase);
        }
        var assigned = 0;
        var unassigned = 0;
        var ignored = 0;
        var invalidTargets = 0;
        foreach (var decision in decisions)
        {
            switch (decision.Action)
            {
                case OrganizationRuleAction.AssignToBox:
                    if (decision.TargetBoxId is not { } target || !validBoxes.Contains(target))
                    {
                        invalidTargets++;
                        continue;
                    }
                    var item = Items.FirstOrDefault(candidate => candidate.Key.ToString() == decision.ItemKey);
                    if (item is not null)
                    {
                        CaptureOriginalIconPositions([item]);
                    }
                    State.Assignments[decision.ItemKey] = target;
                    MoveItemOrderKey(decision.ItemKey, target);
                    assigned++;
                    break;
                case OrganizationRuleAction.KeepUnassigned:
                    if (State.Organization.ReassignExistingItems && State.Assignments.ContainsKey(decision.ItemKey))
                    {
                        UnassignItemCore(decision.ItemKey);
                        unassigned++;
                    }
                    break;
                case OrganizationRuleAction.Ignore:
                    ignored++;
                    break;
            }
        }

        var result = new OrganizationApplyResult(assigned, unassigned, ignored, invalidTargets, decisions);
        if (notify)
        {
            NotifyWorkspaceChanged(true);
        }
        else if (assigned > 0 || unassigned > 0)
        {
            ScheduleSave();
        }
        return result;
    }

    public OrganizationApplyResult SmartOrganize()
    {
        var currentKeys = Items.Select(item => item.Key.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var staleKey in State.Assignments.Keys.Where(key => !currentKeys.Contains(key)).ToArray())
        {
            State.Assignments.Remove(staleKey);
            MoveItemOrderKey(staleKey, null);
        }
        EnsureSmartOrganizationStructure();
        State.Organization.Enabled = true;
        var result = ApplyOrganizationRules(false);
        NotifyWorkspaceChanged(true);
        return result;
    }

    public IReadOnlyList<OrganizationDecision> PreviewOrganizationRules() =>
        _organizationRuleEngine.Preview(State, Items, State.Organization.ReassignExistingItems);

    public IReadOnlyList<OrganizationRuleConflict> GetOrganizationRuleConflicts() =>
        _organizationRuleEngine.FindConflicts(State);

    public void UndoLastOrganization()
    {
        if (_lastOrganizationAssignments is not { } previous)
        {
            return;
        }
        _lastOrganizationAssignments = null;

        foreach (var key in State.Assignments.Keys.Where(key => !previous.ContainsKey(key)).ToArray())
        {
            UnassignItemCore(key);
        }
        foreach (var (key, target) in previous)
        {
            if (State.Assignments.TryGetValue(key, out var current) && current == target)
            {
                continue;
            }
            var item = Items.FirstOrDefault(candidate => candidate.Key.ToString() == key);
            if (item is not null)
            {
                CaptureOriginalIconPositions([item]);
            }
            State.Assignments[key] = target;
            MoveItemOrderKey(key, target);
        }
        NotifyWorkspaceChanged(true);
    }

    public void InstallDefaultOrganizationRules()
    {
        EnsureSmartOrganizationStructure();
        NotifyWorkspaceChanged(true);
    }

    public void SetOrganizationRuleEnabled(Guid ruleId, bool enabled)
    {
        var rule = State.OrganizationRules.FirstOrDefault(candidate => candidate.Id == ruleId);
        if (rule is null || rule.Enabled == enabled)
        {
            return;
        }
        rule.Enabled = enabled;
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public void SaveOrganizationRule(OrganizationRule editedRule)
    {
        var existing = State.OrganizationRules.FirstOrDefault(rule => rule.Id == editedRule.Id);
        if (existing is null)
        {
            existing = new OrganizationRule
            {
                Id = editedRule.Id == Guid.Empty ? Guid.NewGuid() : editedRule.Id,
                Priority = State.OrganizationRules.Count == 0
                    ? 10
                    : State.OrganizationRules.Max(rule => rule.Priority) + 10
            };
            State.OrganizationRules.Add(existing);
        }

        existing.Title = string.IsNullOrWhiteSpace(editedRule.Title) ? "未命名规则" : editedRule.Title.Trim();
        existing.Enabled = editedRule.Enabled;
        existing.ItemKinds = editedRule.ItemKinds.Distinct().ToList();
        existing.NamePattern = string.IsNullOrWhiteSpace(editedRule.NamePattern) ? "*" : editedRule.NamePattern.Trim();
        existing.Extensions = editedRule.Extensions
            .Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Select(NormalizeRuleExtension)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        existing.Action = editedRule.Action;
        existing.TargetBoxId = editedRule.TargetBoxId;
        NormalizeRulePriorities();
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public OrganizationRule? DuplicateOrganizationRule(Guid ruleId)
    {
        var source = State.OrganizationRules.FirstOrDefault(rule => rule.Id == ruleId);
        if (source is null)
        {
            return null;
        }
        var copy = CloneRule(source);
        copy.Id = Guid.NewGuid();
        copy.Title += " 副本";
        copy.Priority = source.Priority + 1;
        State.OrganizationRules.Add(copy);
        NormalizeRulePriorities();
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
        return copy;
    }

    public void DeleteOrganizationRule(Guid ruleId)
    {
        State.OrganizationRules.RemoveAll(rule => rule.Id == ruleId);
        NormalizeRulePriorities();
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public void MoveOrganizationRule(Guid ruleId, int direction)
    {
        var ordered = State.OrganizationRules.OrderBy(rule => rule.Priority).ToList();
        var index = ordered.FindIndex(rule => rule.Id == ruleId);
        if (index < 0 || ordered.Count < 2)
        {
            return;
        }
        var target = Math.Clamp(index + Math.Sign(direction), 0, ordered.Count - 1);
        if (target == index)
        {
            return;
        }
        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        State.OrganizationRules = ordered;
        NormalizeRulePriorities();
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public void SetBoxIconSize(Guid? boxId, double value)
    {
        foreach (var box in GetAppearanceTargets(boxId))
        {
            box.Appearance.IconSize = Math.Clamp(value, 24, 96);
        }
        NotifyWorkspaceChanged(true);
    }

    public void SetBoxLabelFontSize(Guid? boxId, double value)
    {
        foreach (var box in GetAppearanceTargets(boxId))
        {
            box.Appearance.LabelFontSize = Math.Clamp(value, 8, 16);
        }
        NotifyWorkspaceChanged(true);
    }

    public void SetBoxShowItemLabels(Guid? boxId, bool enabled)
    {
        foreach (var box in GetAppearanceTargets(boxId))
        {
            box.Appearance.ShowItemLabels = enabled;
        }
        NotifyWorkspaceChanged(true);
    }

    public void SetBoxShowShortcutBadges(Guid? boxId, bool enabled)
    {
        foreach (var box in GetAppearanceTargets(boxId))
        {
            box.Appearance.ShowShortcutBadges = enabled;
        }
        NotifyWorkspaceChanged(true);
    }

    public void ResetAppearance()
    {
        State.Settings.Appearance = new GlobalAppearanceSettings();
        foreach (var box in State.Boxes)
        {
            box.Appearance = new BoxAppearance();
        }
        NotifyWorkspaceChanged(true);
    }

    public void SetBoxViewMode(Guid? boxId, BoxViewMode mode)
    {
        foreach (var box in GetAppearanceTargets(boxId))
        {
            box.ViewMode = mode;
        }
        NotifyWorkspaceChanged(true);
    }

    public void SetBoxSortMode(Guid? boxId, BoxSortMode mode)
    {
        foreach (var box in GetAppearanceTargets(boxId))
        {
            box.SortMode = mode;
        }
        NotifyWorkspaceChanged(true);
    }

    public void SetThemeMode(ApplicationThemeMode mode)
    {
        if (State.Settings.ThemeMode == mode)
        {
            return;
        }

        State.Settings.ThemeMode = mode;
        ApplyTheme(true);
        ScheduleSave();
    }

    public async Task ReconnectDesktopAsync()
    {
        var hostChanged = _desktopHost.Refresh();
        if (_desktopDoubleClickMonitor is not null)
        {
            _desktopDoubleClickMonitor.DesktopListView = _desktopHost.DesktopListView;
        }
        if (hostChanged)
        {
            RestoreOriginalIconPositions(false);
        }
        Monitors = _monitorService.GetMonitors();
        LayoutCoordinator.NormalizeForMonitors(State, Monitors);
        if (!IsPaused)
        {
            _iconVisibility.SetIconsHidden(true);
            RebuildSurfaces();
        }
        await RefreshItemsAsync(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RequestShowSettings() => ShowSettingsRequested?.Invoke(this, EventArgs.Empty);
    public void RequestExit() => ExitRequested?.Invoke(this, EventArgs.Empty);

    public void NotifyMinimizedToTray()
    {
        if (_trayIcon is null || _trayHintShown)
        {
            return;
        }

        _trayHintShown = true;
        _trayIcon.ShowBalloonTip(
            1800,
            "CrabDesk",
            "CrabDesk 正在系统托盘运行",
            System.Windows.Forms.ToolTipIcon.None);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _hostTimer.Stop();
        _saveTimer.Stop();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _surfaceManager?.Dispose();
        RestoreOriginalIconPositions(true);
        _itemProvider.Dispose();
        _mappedFolderProvider.Dispose();
        _hotkeyService.Pressed -= OnGlobalHotkeyPressed;
        _hotkeyService.Dispose();
        if (_desktopDoubleClickMonitor is not null)
        {
            _desktopDoubleClickMonitor.EmptyAreaDoubleClicked -= OnDesktopEmptyAreaDoubleClicked;
            _desktopDoubleClickMonitor.Dispose();
        }
        _updateCancellation.Cancel();
        if (_updateLock.Wait(0))
        {
            _updateService.Dispose();
            _updateLock.Release();
            _updateCancellation.Dispose();
        }
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
        }
        _trayIcon?.Dispose();
        _trayMenu?.Dispose();
        _applicationIcon?.Dispose();
        if (_originalIconStateCaptured)
        {
            _iconVisibility.SetIconsHidden(_originalIconsHidden);
            if (_recoveryMarker is not null)
            {
                try
                {
                    File.Delete(_recoveryMarker);
                }
                catch
                {
                }
            }
        }
        SaveNowAsync().GetAwaiter().GetResult();
        _saveLock.Dispose();
        _mappedRefreshLock.Dispose();
    }

    private void StartTakeover()
    {
        IsPaused = false;
        AreDesktopItemsHidden = false;
        _originalIconsHidden = State.MigratedFromLegacyIconTakeover
            ? false
            : _iconVisibility.GetIconsHidden();
        _originalIconStateCaptured = true;
        EnsureRecoveryGuard();
        _iconVisibility.SetIconsHidden(true);
        RebuildSurfaces();
    }

    private void EnsureRecoveryGuard()
    {
        WriteRecoveryMarker();
        if (_guardStarted)
        {
            return;
        }

        var guardPath = Path.Combine(AppContext.BaseDirectory, "CrabDesk.IconGuard.exe");
        if (!File.Exists(guardPath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(guardPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = $"{Environment.ProcessId} {_originalIconsHidden} \"{_recoveryMarker}\""
        });
        _guardStarted = true;
    }

    private void RecoverStaleIconState()
    {
        var marker = Path.Combine(Path.GetDirectoryName(_layoutStore.StatePath)!, "desktop-visibility.lock");
        if (!File.Exists(marker))
        {
            return;
        }

        try
        {
            var recovery = JsonSerializer.Deserialize<DesktopRecoveryState>(File.ReadAllText(marker));
            if (recovery is not null)
            {
                _iconVisibility.SetIconsHidden(recovery.PreviousHidden);
                DesktopIconPositionService.RestoreItemPositions(
                    _desktopHost.DesktopListView,
                    recovery.IconPositions ?? []);
            }
            File.Delete(marker);
        }
        catch
        {
        }
    }

    private void RebuildSurfaces()
    {
        _surfaceManager?.Dispose();
        _surfaceManager = null;
        if (!_desktopHost.IsAvailable || Monitors.Count == 0)
        {
            return;
        }
        CaptureOriginalIconPositions(Items.Where(item => State.Assignments.ContainsKey(item.Key.ToString())));
        _surfaceManager = new DesktopSurfaceManager(this, _desktopHost, Monitors);
    }

    private void NormalizeMonitorIds()
    {
        if (Monitors.Count == 0)
        {
            return;
        }
        var primary = Monitors.FirstOrDefault(candidate => candidate.IsPrimary) ?? Monitors[0];
        foreach (var box in State.Boxes.Where(box => string.IsNullOrEmpty(box.MonitorId) || box.MonitorId == "primary"))
        {
            box.MonitorId = primary.Id;
        }
    }

    private IEnumerable<DesktopBox> GetAppearanceTargets(Guid? boxId) => boxId is null
        ? State.Boxes
        : State.Boxes.Where(box => box.Id == boxId.Value);

    private void EnsureSmartOrganizationStructure()
    {
        var monitor = Monitors.FirstOrDefault(candidate => candidate.IsPrimary) ?? Monitors.First();
        var active = new List<(DesktopBox Box, int ItemCount)>();
        foreach (var spec in SmartBoxSpecs)
        {
            var matchingItems = Items.Where(item => MatchesSmartSpec(item, spec)).ToArray();
            var rule = State.OrganizationRules.FirstOrDefault(candidate =>
                string.Equals(candidate.Title, spec.Title, StringComparison.CurrentCultureIgnoreCase));
            var box = rule?.TargetBoxId is { } target
                ? State.Boxes.FirstOrDefault(candidate => candidate.Id == target && !candidate.IsMappedFolder)
                : State.Boxes.FirstOrDefault(candidate => candidate.IsAutoGenerated &&
                    string.Equals(candidate.Title, spec.Title, StringComparison.CurrentCultureIgnoreCase));

            if (matchingItems.Length == 0)
            {
                if (box is not null &&
                    (box.IsAutoGenerated || rule?.TargetBoxId == box.Id) &&
                    !State.Assignments.Values.Contains(box.Id))
                {
                    State.Boxes.Remove(box);
                }
                if (rule is not null && (box is null || rule.TargetBoxId == box.Id))
                {
                    State.OrganizationRules.Remove(rule);
                }
                continue;
            }

            box ??= new DesktopBox
            {
                Title = spec.Title,
                MonitorId = monitor.Id,
                IsAutoGenerated = true,
                Appearance = new BoxAppearance { Accent = spec.Accent }
            };
            box.Title = spec.Title;
            box.MonitorId = monitor.Id;
            box.IsAutoGenerated = true;
            box.Appearance.Accent = spec.Accent;
            if (!State.Boxes.Contains(box))
            {
                State.Boxes.Add(box);
            }

            rule ??= new OrganizationRule { Title = spec.Title };
            rule.Enabled = true;
            rule.Priority = spec.Priority;
            rule.ItemKinds = spec.Kinds.ToList();
            rule.Extensions = spec.Extensions.ToList();
            rule.NamePattern = "*";
            rule.Action = OrganizationRuleAction.AssignToBox;
            rule.TargetBoxId = box.Id;
            if (!State.OrganizationRules.Contains(rule))
            {
                State.OrganizationRules.Add(rule);
            }
            active.Add((box, matchingItems.Length));
        }

        var builtInTitles = SmartBoxSpecs.Select(spec => spec.Title).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        foreach (var rule in State.OrganizationRules.Where(rule =>
                     rule.Enabled && rule.Action == OrganizationRuleAction.AssignToBox &&
                     !builtInTitles.Contains(rule.Title)).ToArray())
        {
            var matchingItems = Items.Where(item => OrganizationRuleEngine.MatchesRule(rule, item)).ToArray();
            if (matchingItems.Length == 0)
            {
                continue;
            }

            var box = rule.TargetBoxId is { } target
                ? State.Boxes.FirstOrDefault(candidate => candidate.Id == target && !candidate.IsMappedFolder)
                : null;
            if (box is null)
            {
                box = State.Boxes.FirstOrDefault(candidate => candidate.IsAutoGenerated &&
                    string.Equals(candidate.Title, rule.Title, StringComparison.CurrentCultureIgnoreCase));
                box ??= new DesktopBox
                {
                    Title = rule.Title,
                    MonitorId = monitor.Id,
                    IsAutoGenerated = true
                };
                if (!State.Boxes.Contains(box))
                {
                    State.Boxes.Add(box);
                }
                rule.TargetBoxId = box.Id;
            }
            if (box.IsAutoGenerated && active.All(entry => entry.Box.Id != box.Id))
            {
                box.MonitorId = monitor.Id;
                active.Add((box, matchingItems.Length));
            }
        }

        var activeIds = active.Select(entry => entry.Box.Id).ToHashSet();
        var occupied = State.Boxes
            .Where(box => string.Equals(box.MonitorId, monitor.Id, StringComparison.OrdinalIgnoreCase) &&
                !activeIds.Contains(box.Id))
            .Select(box => box.Bounds)
            .ToArray();
        var requested = active.Select(entry => new LayoutRect(
            0,
            0,
            360,
            Math.Clamp(82 + Math.Ceiling(entry.ItemCount / 4d) * 88, 190, 366))).ToArray();
        var arranged = BoxLayoutPlanner.Arrange(monitor.WorkArea, requested, occupied);
        for (var index = 0; index < active.Count; index++)
        {
            active[index].Box.Bounds = arranged[index];
        }
        NormalizeRulePriorities();
    }

    private LayoutRect FindAvailableBoxBounds(MonitorLayout monitor, double width, double height)
    {
        var occupied = State.Boxes
            .Where(box => string.Equals(box.MonitorId, monitor.Id, StringComparison.OrdinalIgnoreCase))
            .Select(box => box.Bounds)
            .ToArray();
        return BoxLayoutPlanner.Arrange(monitor.WorkArea, [new LayoutRect(0, 0, width, height)], occupied)[0];
    }

    private static bool MatchesSmartSpec(DesktopItemRef item, SmartBoxSpec spec)
    {
        if (!spec.Kinds.Contains(item.Kind))
        {
            return false;
        }
        if (spec.Extensions.Count == 0)
        {
            return true;
        }
        var extension = Path.GetExtension(item.FileSystemPath ?? item.DisplayName).TrimStart('.');
        return spec.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private void NormalizeRulePriorities()
    {
        var ordered = State.OrganizationRules.OrderBy(rule => rule.Priority).ToArray();
        State.OrganizationRules = ordered.ToList();
        for (var index = 0; index < State.OrganizationRules.Count; index++)
        {
            State.OrganizationRules[index].Priority = (index + 1) * 10;
        }
    }

    private static OrganizationRule CloneRule(OrganizationRule source) => new()
    {
        Id = source.Id,
        Title = source.Title,
        Enabled = source.Enabled,
        Priority = source.Priority,
        ItemKinds = source.ItemKinds.ToList(),
        NamePattern = source.NamePattern,
        Extensions = source.Extensions.ToList(),
        Action = source.Action,
        TargetBoxId = source.TargetBoxId
    };

    private static string NormalizeRuleExtension(string value)
    {
        var extension = value.Trim();
        return extension.StartsWith('.') ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
    }

    private void NotifyWorkspaceChanged(bool rebuild)
    {
        if (rebuild)
        {
            _surfaceManager?.Refresh();
        }
        else
        {
            _surfaceManager?.UpdateRegions();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    private async Task RefreshMappedFoldersAsync(bool notify = true)
    {
        var changed = false;
        await _mappedRefreshLock.WaitAsync();
        try
        {
            ConfigureMappedFolderWatchers();
            var mappedBoxes = State.Boxes.Where(box => box.IsMappedFolder).ToArray();
            var validIds = mappedBoxes.Select(box => box.Id).ToHashSet();
            foreach (var staleId in _mappedFolderSnapshots.Keys.Where(id => !validIds.Contains(id)).ToArray())
            {
                _mappedFolderSnapshots.Remove(staleId);
                changed = true;
            }

            foreach (var box in mappedBoxes)
            {
                var snapshot = await _mappedFolderProvider.EnumerateAsync(box.MappedFolder!.Path);
                changed |= !_mappedFolderSnapshots.TryGetValue(box.Id, out var previous) ||
                    !MappedSnapshotsEqual(previous, snapshot);
                _mappedFolderSnapshots[box.Id] = snapshot;
            }
            _lastMappedHealthCheckAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _mappedRefreshLock.Release();
        }

        if (notify && changed)
        {
            _surfaceManager?.Refresh();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ConfigureMappedFolderWatchers()
    {
        _mappedFolderProvider.SetWatchedFolders(State.Boxes
            .Where(box => box.MappedFolder is not null)
            .Select(box => box.MappedFolder!.Path));
    }

    private async void OnHostTimer(object? sender, EventArgs eventArgs)
    {
        if (_disposed || _hostCheckInProgress)
        {
            return;
        }

        _hostCheckInProgress = true;
        try
        {
            if (State.Boxes.Any(box => box.IsMappedFolder) &&
                (_mappedFolderSnapshots.Values.Any(snapshot => !snapshot.IsAvailable) ||
                 DateTimeOffset.UtcNow - _lastMappedHealthCheckAt >= TimeSpan.FromSeconds(10)))
            {
                await RefreshMappedFoldersAsync();
            }
            var hostChanged = _desktopHost.Refresh();
            if (_desktopDoubleClickMonitor is not null)
            {
                _desktopDoubleClickMonitor.DesktopListView = _desktopHost.DesktopListView;
            }
            var monitors = _monitorService.GetMonitors();
            var topologyChanged = !monitors.Select(monitor => $"{monitor.Id}:{monitor.PixelBounds}")
                .SequenceEqual(Monitors.Select(monitor => $"{monitor.Id}:{monitor.PixelBounds}"));
            if (!hostChanged && !topologyChanged)
            {
                // This timer is a health check. Repainting here causes a visible desktop flash every two seconds.
                return;
            }

            Monitors = monitors;
            if (hostChanged)
            {
                RestoreOriginalIconPositions(false);
            }
            NormalizeMonitorIds();
            LayoutCoordinator.NormalizeForMonitors(State, Monitors);
            if (!IsPaused)
            {
                _iconVisibility.SetIconsHidden(true);
                RebuildSurfaces();
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _hostCheckInProgress = false;
        }
    }

    private static bool MappedSnapshotsEqual(MappedFolderSnapshot left, MappedFolderSnapshot right)
    {
        if (!string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase) ||
            left.Availability != right.Availability ||
            left.Items.Count != right.Items.Count)
        {
            return false;
        }

        return left.Items.Zip(right.Items).All(pair =>
            pair.First.Key == pair.Second.Key &&
            string.Equals(pair.First.DisplayName, pair.Second.DisplayName, StringComparison.Ordinal) &&
            pair.First.ModifiedAt == pair.Second.ModifiedAt);
    }

    private void ApplyHotkeys()
    {
        ApplyHotkey(HotkeyAction.ShowDesktop, State.Settings.Hotkeys.ShowDesktop);
        ApplyHotkey(HotkeyAction.OrganizeDesktop, State.Settings.Hotkeys.OrganizeDesktop);
    }

    private void ApplyHotkey(HotkeyAction action, HotkeyBinding binding)
    {
        try
        {
            _hotkeyStatuses[action] = _hotkeyService.Register(action, binding);
        }
        catch
        {
            _hotkeyStatuses[action] = HotkeyRegistrationStatus.Failed;
        }
    }

    private HotkeyBinding GetHotkeyBinding(HotkeyAction action) => action switch
    {
        HotkeyAction.ShowDesktop => State.Settings.Hotkeys.ShowDesktop,
        HotkeyAction.OrganizeDesktop => State.Settings.Hotkeys.OrganizeDesktop,
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private void OnGlobalHotkeyPressed(object? sender, GlobalHotkeyPressedEventArgs eventArgs)
    {
        _dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (eventArgs.Action == HotkeyAction.ShowDesktop)
                {
                    DesktopWindowTools.ToggleDesktop();
                    return;
                }

                var result = SmartOrganize();
                _trayIcon?.ShowBalloonTip(
                    1800,
                    "CrabDesk",
                    $"整理完成：分配 {result.Assigned} 个，移出 {result.Unassigned} 个",
                    System.Windows.Forms.ToolTipIcon.None);
            }
            catch (Exception exception)
            {
                _trayIcon?.ShowBalloonTip(
                    2200,
                    "CrabDesk",
                    exception.Message,
                    System.Windows.Forms.ToolTipIcon.Error);
            }
        });
    }

    private void ConfigureDesktopDoubleClickMonitor(bool enabled)
    {
        if (!enabled)
        {
            if (_desktopDoubleClickMonitor is not null)
            {
                _desktopDoubleClickMonitor.Enabled = false;
            }
            return;
        }

        if (_desktopDoubleClickMonitor is null)
        {
            _desktopDoubleClickMonitor = new DesktopDoubleClickMonitor();
            _desktopDoubleClickMonitor.EmptyAreaDoubleClicked += OnDesktopEmptyAreaDoubleClicked;
        }
        _desktopDoubleClickMonitor.DesktopListView = _desktopHost.DesktopListView;
        _desktopDoubleClickMonitor.Enabled = true;
    }

    private void OnDesktopEmptyAreaDoubleClicked(object? sender, EventArgs eventArgs)
    {
        _dispatcher.BeginInvoke(() =>
        {
            AreDesktopItemsHidden = !AreDesktopItemsHidden;
            _iconVisibility.SetIconsHidden(true);
            _surfaceManager?.Refresh();
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnSystemPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs eventArgs)
    {
        if (State.Settings.ThemeMode != ApplicationThemeMode.System || _disposed)
        {
            return;
        }

        _dispatcher.BeginInvoke(() => ApplyTheme(true));
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs eventArgs)
    {
        if (_disposed)
        {
            return;
        }
        if (eventArgs.Mode == Microsoft.Win32.PowerModes.Suspend)
        {
            _dispatcher.BeginInvoke(async () =>
            {
                if (_disposed)
                {
                    return;
                }
                if (_originalIconStateCaptured && !IsPaused)
                {
                    WriteRecoveryMarker();
                }
                await SaveNowAsync();
            });
            return;
        }
        if (eventArgs.Mode != Microsoft.Win32.PowerModes.Resume)
        {
            return;
        }

        _dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(1200);
            if (_disposed)
            {
                return;
            }
            await ReconnectDesktopAsync();
        });
    }

    private async Task RunDailyBackupIfNeededAsync()
    {
        var settings = State.Settings.Backup;
        if (!settings.DailyBackup || settings.LastBackupAt?.LocalDateTime.Date == DateTime.Today)
        {
            return;
        }

        var service = GetBackupService();
        await service.CreateAsync(State);
        settings.LastBackupAt = DateTimeOffset.Now;
        await service.CleanupAsync(settings.RetentionDays);
    }

    private IBackupService GetBackupService()
    {
        var configured = State.Settings.Backup.BackupDirectory;
        var directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetDirectoryName(_layoutStore.StatePath)!, "Backups")
            : Environment.ExpandEnvironmentVariables(configured);
        return new JsonBackupService(directory);
    }

    private async Task ApplyLoadedStateAsync(CrabDeskState state)
    {
        _surfaceManager?.Dispose();
        _surfaceManager = null;
        RestoreOriginalIconPositions(true);
        State = state;
        _lastOrganizationAssignments = null;
        LastUpdateCheck = new UpdateCheckResult(UpdateCheckStatus.NotChecked, CurrentVersion);
        AreDesktopItemsHidden = false;
        StartupRegistration.SetEnabled(State.Settings.StartWithWindows);
        ApplyHotkeys();
        _desktopContextMenu.SetEnabled(
            State.Settings.DesktopBehavior.ShowDesktopContextMenu,
            Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CrabDesk.App.exe"));
        ConfigureDesktopDoubleClickMonitor(State.Settings.DesktopBehavior.ToggleIconsOnDesktopDoubleClick);
        ApplyTheme(false);
        Monitors = _monitorService.GetMonitors();
        NormalizeMonitorIds();
        LayoutCoordinator.NormalizeForMonitors(State, Monitors);
        await RefreshItemsAsync(false);

        IsPaused = !State.Settings.TakeOverDesktop;
        if (IsPaused)
        {
            _iconVisibility.SetIconsHidden(_originalIconsHidden);
        }
        else
        {
            EnsureRecoveryGuard();
            _iconVisibility.SetIconsHidden(true);
            RebuildSurfaces();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyTheme(bool notify)
    {
        IsDarkTheme = ApplicationTheme.ResolveIsDark(State.Settings.ThemeMode);
        ApplicationTheme.ApplyResources(IsDarkTheme);
        _surfaceManager?.Refresh();
        UpdateTrayMenu();
        if (notify)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async void OnSaveTimer(object? sender, EventArgs eventArgs)
    {
        _saveTimer.Stop();
        await SaveNowAsync();
    }

    private async Task SaveNowAsync()
    {
        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _layoutStore.SaveAsync(State).ConfigureAwait(false);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private void CreateTrayIcon()
    {
        _trayMenu = new System.Windows.Forms.ContextMenuStrip();
        _trayMenu.Opening += (_, _) => UpdateTrayMenu();

        var showSettingsItem = new System.Windows.Forms.ToolStripMenuItem(
            "打开 CrabDesk",
            null,
            (_, _) => _dispatcher.BeginInvoke(RequestShowSettings));
        showSettingsItem.Font = new System.Drawing.Font(showSettingsItem.Font, System.Drawing.FontStyle.Bold);
        _trayMenu.Items.Add(showSettingsItem);
        _trayMenu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(
            "智能整理",
            null,
            (_, _) => _dispatcher.BeginInvoke(() => SmartOrganize())));
        _trayMenu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(
            "新建盒子",
            null,
            (_, _) => _dispatcher.BeginInvoke(() => AddBox())));
        _trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        _pauseTrayItem = new System.Windows.Forms.ToolStripMenuItem(
            "暂停桌面接管",
            null,
            (_, _) => _dispatcher.BeginInvoke(() =>
            {
                SetPaused(!IsPaused);
                UpdateTrayMenu();
            }));
        _trayMenu.Items.Add(_pauseTrayItem);
        _trayMenu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(
            "重新连接桌面",
            null,
            (_, _) => _dispatcher.BeginInvoke(async () => await ReconnectDesktopAsync())));

        _startupTrayItem = new System.Windows.Forms.ToolStripMenuItem(
            "开机启动",
            null,
            (_, _) => _dispatcher.BeginInvoke(() =>
            {
                SetStartWithWindows(!State.Settings.StartWithWindows);
                UpdateTrayMenu();
            }));
        _trayMenu.Items.Add(_startupTrayItem);

        var themeMenu = new System.Windows.Forms.ToolStripMenuItem("主题");
        AddThemeTrayItem(themeMenu, "跟随系统", ApplicationThemeMode.System);
        AddThemeTrayItem(themeMenu, "浅色", ApplicationThemeMode.Light);
        AddThemeTrayItem(themeMenu, "深色", ApplicationThemeMode.Dark);
        _trayMenu.Items.Add(themeMenu);
        _trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _trayMenu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(
            "退出 CrabDesk",
            null,
            (_, _) => _dispatcher.BeginInvoke(RequestExit)));

        _applicationIcon = LoadApplicationIcon();
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "CrabDesk 桌面整理",
            Icon = _applicationIcon ?? System.Drawing.SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = _trayMenu
        };
        _trayIcon.DoubleClick += (_, _) => _dispatcher.BeginInvoke(RequestShowSettings);
        _trayIcon.MouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == System.Windows.Forms.MouseButtons.Left)
            {
                _dispatcher.BeginInvoke(RequestShowSettings);
            }
        };
        UpdateTrayMenu();
    }

    private void UpdateTrayMenu()
    {
        if (_pauseTrayItem is not null)
        {
            _pauseTrayItem.Text = IsPaused ? "恢复桌面接管" : "暂停桌面接管";
            _pauseTrayItem.Checked = IsPaused;
        }
        if (_startupTrayItem is not null)
        {
            _startupTrayItem.Checked = State.Settings.StartWithWindows;
        }
        foreach (var (mode, item) in _themeTrayItems)
        {
            item.Checked = State.Settings.ThemeMode == mode;
        }
        if (_trayMenu is not null)
        {
            _trayMenu.Renderer = IsDarkTheme ? _darkTrayRenderer : _lightTrayRenderer;
            ApplyTrayColors(_trayMenu.Items, IsDarkTheme);
            _trayMenu.BackColor = IsDarkTheme
                ? System.Drawing.Color.FromArgb(37, 40, 45)
                : System.Drawing.Color.White;
            _trayMenu.ForeColor = IsDarkTheme
                ? System.Drawing.Color.FromArgb(244, 245, 247)
                : System.Drawing.Color.FromArgb(32, 36, 42);
        }
    }

    private void AddThemeTrayItem(
        System.Windows.Forms.ToolStripMenuItem parent,
        string title,
        ApplicationThemeMode mode)
    {
        var item = new System.Windows.Forms.ToolStripMenuItem(
            title,
            null,
            (_, _) => _dispatcher.BeginInvoke(() => SetThemeMode(mode)));
        _themeTrayItems[mode] = item;
        parent.DropDownItems.Add(item);
    }

    private static void ApplyTrayColors(System.Windows.Forms.ToolStripItemCollection items, bool isDark)
    {
        var background = isDark
            ? System.Drawing.Color.FromArgb(37, 40, 45)
            : System.Drawing.Color.White;
        var foreground = isDark
            ? System.Drawing.Color.FromArgb(244, 245, 247)
            : System.Drawing.Color.FromArgb(32, 36, 42);
        foreach (System.Windows.Forms.ToolStripItem item in items)
        {
            item.BackColor = background;
            item.ForeColor = foreground;
            if (item is System.Windows.Forms.ToolStripMenuItem menuItem)
            {
                ApplyTrayColors(menuItem.DropDownItems, isDark);
            }
        }
    }

    private static System.Drawing.Icon? LoadApplicationIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/CrabDesk.ico"));
            if (resource is null)
            {
                return null;
            }
            using (resource.Stream)
            using (var icon = new System.Drawing.Icon(resource.Stream))
            {
                return (System.Drawing.Icon)icon.Clone();
            }
        }
        catch
        {
            return Environment.ProcessPath is { } processPath
                ? System.Drawing.Icon.ExtractAssociatedIcon(processPath)
                : null;
        }
    }

    private void CaptureOriginalIconPositions(IEnumerable<DesktopItemRef> items)
    {
        if (_desktopHost.DesktopListView == IntPtr.Zero)
        {
            return;
        }

        var uncaptured = items
            .Where(item => !_originalIconPositions.ContainsKey(item.Key.ToString()))
            .Select(item => new
            {
                Item = item,
                Names = GetExplorerNames(item)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(NormalizeExplorerName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            })
            .Where(entry => entry.Names.Length > 0)
            .ToArray();
        if (uncaptured.Length == 0)
        {
            return;
        }

        var captured = DesktopIconPositionService.CaptureItemPositions(
            _desktopHost.DesktopListView,
            uncaptured.SelectMany(entry => entry.Names));
        var positionsByName = captured
            .GroupBy(position => NormalizeExplorerName(position.DisplayName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var changed = false;
        foreach (var entry in uncaptured)
        {
            DesktopIconPositionSnapshot? position = null;
            foreach (var name in entry.Names)
            {
                if (positionsByName.TryGetValue(name, out var candidate))
                {
                    position = candidate;
                    break;
                }
            }
            if (position is not { } capturedPosition)
            {
                continue;
            }

            _originalIconPositions[entry.Item.Key.ToString()] = capturedPosition;
            changed = true;
        }

        if (changed && _originalIconStateCaptured)
        {
            WriteRecoveryMarker();
        }
    }

    private void RestoreOriginalIconPositions(bool clear)
    {
        if (_originalIconPositions.Count == 0)
        {
            return;
        }

        DesktopIconPositionService.RestoreItemPositions(
            _desktopHost.DesktopListView,
            _originalIconPositions.Values);
        if (clear)
        {
            _originalIconPositions.Clear();
            if (_originalIconStateCaptured)
            {
                WriteRecoveryMarker();
            }
        }
    }

    private void UnassignItemCore(string itemKey)
    {
        if (_originalIconPositions.TryGetValue(itemKey, out var position))
        {
            DesktopIconPositionService.RestoreItemPositions(_desktopHost.DesktopListView, [position]);
        }
        State.Assignments.Remove(itemKey);
        MoveItemOrderKey(itemKey, null);
    }

    private void WriteRecoveryMarker()
    {
        var root = Path.GetDirectoryName(_layoutStore.StatePath)!;
        _recoveryMarker ??= Path.Combine(root, "desktop-visibility.lock");
        var recovery = new DesktopRecoveryState
        {
            PreviousHidden = _originalIconsHidden,
            IconPositions = _originalIconPositions.Values.ToList()
        };
        File.WriteAllText(_recoveryMarker, JsonSerializer.Serialize(recovery));
    }

    private static IEnumerable<string> GetExplorerNames(DesktopItemRef item)
    {
        yield return item.DisplayName;
        if (item.FileSystemPath is not null)
        {
            yield return Path.GetFileName(item.FileSystemPath);
        }
    }

    private static string NormalizeExplorerName(string value) => value.Trim().TrimEnd('.');

    private async void OnDesktopItemsChanged()
    {
        if (_disposed)
        {
            return;
        }
        var realtimeOrganization = State.Organization.Enabled && State.Organization.RunOnDesktopChanges;
        if (!State.Settings.DesktopBehavior.RefreshAfterRename && !realtimeOrganization)
        {
            return;
        }
        await RefreshItemsAsync();
    }

    private void MoveItemOrderKey(string itemKey, Guid? targetBoxId)
    {
        foreach (var box in State.Boxes)
        {
            box.ItemOrder.RemoveAll(key => string.Equals(key, itemKey, StringComparison.OrdinalIgnoreCase));
        }
        if (targetBoxId is { } target)
        {
            State.Boxes.FirstOrDefault(box => box.Id == target)?.ItemOrder.Add(itemKey);
        }
    }

    private void ReplaceItemOrderKey(string oldKey, string newKey)
    {
        foreach (var box in State.Boxes)
        {
            for (var index = 0; index < box.ItemOrder.Count; index++)
            {
                if (string.Equals(box.ItemOrder[index], oldKey, StringComparison.OrdinalIgnoreCase))
                {
                    box.ItemOrder[index] = newKey;
                }
            }
        }
    }

    private static string FormatHandle(IntPtr handle) => $"0x{handle.ToInt64():X}";

    private sealed record SmartBoxSpec(
        string Title,
        string Accent,
        int Priority,
        IReadOnlyList<DesktopItemKind> Kinds,
        IReadOnlyList<string> Extensions);
}
