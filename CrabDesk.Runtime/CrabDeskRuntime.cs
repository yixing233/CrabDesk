using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using CrabDesk.Core;
using CrabDesk.Native;

namespace CrabDesk.Runtime;

public sealed class ShowSettingsRequestedEventArgs(string? page) : EventArgs
{
    public string? Page { get; } = page;
}

public sealed class CrabDeskRuntime : IDisposable
{
    private readonly Action<Action> _beginInvoke;
    private readonly ILayoutStore _layoutStore = new JsonLayoutStore();
    private readonly IMonitorTopologyService _monitorService = new MonitorTopologyService();
    private readonly DesktopHostService _desktopHost = new();
    private readonly IDesktopItemProvider _itemProvider = new DesktopItemProvider();
    private readonly IMappedFolderProvider _mappedFolderProvider = new MappedFolderProvider();
    private readonly IFileOperationService _fileOperations = new FileOperationService();
    private readonly IHotkeyService _hotkeyService = new GlobalHotkeyService();
    private readonly IDesktopContextMenuRegistration _desktopContextMenu = new DesktopContextMenuRegistration();
    private IDesktopInputMonitor? _desktopInputMonitor;
    private readonly IOrganizationRuleEngine _organizationRuleEngine = new OrganizationRuleEngine();
    private readonly AiClassificationService _aiClassificationService = new();
    private readonly IUpdateService _updateService = new GitHubUpdateService();
    private readonly ShellIconProvider _iconProvider = new();
    private readonly RuntimeTimer _hostTimer;
    private readonly RuntimeTimer _saveTimer;
    private readonly RuntimeTimer _desktopZoomTimer;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly SemaphoreSlim _mappedRefreshLock = new(1, 1);
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly CancellationTokenSource _updateCancellation = new();
    private readonly Dictionary<Guid, MappedFolderSnapshot> _mappedFolderSnapshots = [];
    private readonly Dictionary<string, DesktopIconPositionSnapshot> _originalIconPositions = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<System.Drawing.Rectangle>? _originalDesktopWorkAreas;
    private IntPtr _parkingWorkAreaListView;
    private readonly Dictionary<HotkeyAction, HotkeyRegistrationStatus> _hotkeyStatuses = [];
    private IReadOnlyList<DesktopItemRef> _allDesktopItems = [];
    private Dictionary<string, Guid>? _lastOrganizationAssignments;
    private DesktopSurfaceManager? _surfaceManager;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Windows.Forms.ContextMenuStrip? _trayMenu;
    private System.Windows.Forms.ToolStripMenuItem? _pauseTrayItem;
    private System.Windows.Forms.ToolStripMenuItem? _startupTrayItem;
    private readonly Dictionary<ApplicationThemeMode, System.Windows.Forms.ToolStripMenuItem> _themeTrayItems = [];
    private readonly ConditionalWeakTable<System.Windows.Forms.ToolStripDropDown, object> _configuredSubmenus = new();
    private readonly FluentMenuRenderer _lightTrayRenderer = new(false);
    private readonly FluentMenuRenderer _darkTrayRenderer = new(true);
    private readonly System.Drawing.Font _menuFont = new("Segoe UI", 10, System.Drawing.FontStyle.Regular);
    private System.Drawing.Icon? _applicationIcon;
    private bool _trayHintShown;
    private bool _disposed;
    private bool _hostCheckInProgress;
    private DateTimeOffset _lastMappedHealthCheckAt;
    private string? _verifiedUpdateInstallerPath;
    private string? _verifiedUpdateSha256;
    private bool _verifiedUpdateIsPrerelease;

    public CrabDeskRuntime(Action<Action> beginInvoke)
    {
        _beginInvoke = beginInvoke;
        _hostTimer = new RuntimeTimer(
            TimeSpan.FromSeconds(2),
            true,
            beginInvoke,
            () => OnHostTimer(null, EventArgs.Empty));
        _saveTimer = new RuntimeTimer(
            TimeSpan.FromMilliseconds(350),
            false,
            beginInvoke,
            () => OnSaveTimer(null, EventArgs.Empty));
        _desktopZoomTimer = new RuntimeTimer(
            TimeSpan.FromMilliseconds(250),
            false,
            beginInvoke,
            SynchronizeDesktopIconZoom);
        _itemProvider.ItemsChanged += (_, _) => _beginInvoke(OnDesktopItemsChanged);
        _mappedFolderProvider.ItemsChanged += (_, _) => _beginInvoke(async () => await RefreshMappedFoldersAsync());
        _hotkeyService.Pressed += OnGlobalHotkeyPressed;
    }

    public event EventHandler? Changed;
    public event EventHandler<ShowSettingsRequestedEventArgs>? ShowSettingsRequested;
    public event EventHandler? ExitRequested;

    public CrabDeskState State { get; private set; } = new();
    public IReadOnlyList<DesktopItemRef> Items { get; private set; } = [];
    public IReadOnlyList<MonitorLayout> Monitors { get; private set; } = [];
    public bool IsPaused { get; private set; }
    public bool IsDarkTheme { get; private set; }
    public bool AreDesktopItemsHidden { get; private set; }
    public bool IsCheckingForUpdates { get; private set; }
    public bool IsDownloadingUpdate { get; private set; }
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
            : System.Drawing.Color.FromArgb(252, 252, 252);
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
        DiagnosticLog.Info("Runtime initialization started");
        State = await _layoutStore.LoadAsync();
        State.Settings.AiClassification.ApiKey = AiApiKeyStore.Load(GetAiApiKeyPath());
        MigrateGlobalHoverExpansionSetting();
        SynchronizeBoxStyles();
        DiagnosticLog.Info($"State loaded schema={State.SchemaVersion} takeover={State.Settings.TakeOverDesktop} boxes={State.Boxes.Count}");
        var updateRepository = UpdateConfiguration.ResolveRepository(State.Settings.Updates);
        if (!string.IsNullOrWhiteSpace(updateRepository.Owner) &&
            !string.IsNullOrWhiteSpace(updateRepository.Repository))
        {
            State.Settings.Updates.RepositoryOwner = updateRepository.Owner;
            State.Settings.Updates.RepositoryName = updateRepository.Repository;
        }
        var cachedUpdate = State.Settings.Updates;
        var cachedStatus = cachedUpdate.LastStatus;
        var cachedMessage = cachedUpdate.LastMessage;
        if (SemanticVersion.TryParse(CurrentVersion, out var currentSemanticVersion) &&
            SemanticVersion.TryParse(cachedUpdate.LatestKnownVersion, out var cachedSemanticVersion) &&
            cachedStatus is UpdateCheckStatus.UpToDate or UpdateCheckStatus.UpdateAvailable)
        {
            cachedStatus = cachedSemanticVersion.CompareTo(currentSemanticVersion) > 0
                ? UpdateCheckStatus.UpdateAvailable
                : UpdateCheckStatus.UpToDate;
        }
        // A stale failed check (for example a legacy 404 from an older build)
        // must not surface as the current state on startup. Degrade it to
        // NotChecked so the user is invited to re-check instead of reading an
        // outdated error forever.
        if (cachedStatus == UpdateCheckStatus.Failed &&
            cachedUpdate.LastCheckedAt is { } lastCheckedAt &&
            DateTimeOffset.Now - lastCheckedAt > TimeSpan.FromHours(6))
        {
            cachedStatus = UpdateCheckStatus.NotChecked;
            cachedMessage = "上次检查更新失败，请点击重新检查";
        }
        LastUpdateCheck = new UpdateCheckResult(
            cachedStatus,
            CurrentVersion,
            cachedUpdate.LatestKnownVersion,
            cachedUpdate.CachedReleaseName,
            cachedUpdate.CachedPublishedAt,
            cachedUpdate.CachedReleaseNotes,
            string.IsNullOrWhiteSpace(cachedUpdate.CachedReleasePageUrl)
                ? GetReleasePageUrl()
                : cachedUpdate.CachedReleasePageUrl,
            cachedUpdate.CachedInstallerUrl,
            cachedUpdate.CachedSha256Url,
            cachedUpdate.CachedIsPrerelease,
            cachedUpdate.CachedETag,
            cachedMessage);
        State.Settings.StartWithWindows = StartupRegistration.IsEnabled();
        ApplyHotkeys();
        // Expose one desktop-level CrabDesk entry whose secondary commands are
        // handled by the application's single-instance command channel.
        try
        {
            _desktopContextMenu.SetEnabled(
                true,
                Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CrabDesk.WinUI.exe"));
        }
        catch
        {
        }
        ApplyTheme(false);
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _desktopHost.Refresh();
        EnsureDesktopInput("startup");
        if (State.Settings.TakeOverDesktop)
        {
            ConfigureDesktopInputMonitor();
        }
        Monitors = _monitorService.GetMonitors();
        NormalizeMonitorIds();
        LayoutCoordinator.NormalizeForMonitors(State, Monitors);
        await RefreshItemsAsync(false);
        await RunScheduledBackupIfNeededAsync();
        RepairHiddenAssignedItemAttributes();
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
        DiagnosticLog.Info($"Runtime initialization completed paused={IsPaused} monitors={Monitors.Count} items={Items.Count}");
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

    private IReadOnlyList<DesktopItemRef> GetAssignedDesktopItems() => _allDesktopItems
        .Where(item => State.Assignments.ContainsKey(item.Key.ToString()))
        .ToArray();

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
        var box = CreateBoxCore(title);
        if (IsPaused)
        {
            SetPaused(false);
        }
        else
        {
            NotifyWorkspaceChanged(true);
        }
        return box;
    }

    private DesktopBox CreateBoxCore(string title)
    {
        var monitor = Monitors.FirstOrDefault(candidate => candidate.IsPrimary) ?? Monitors.First();
        var shared = State.Boxes.FirstOrDefault();
        var box = new DesktopBox
        {
            Title = title,
            MonitorId = monitor.Id,
            Bounds = FindAvailableBoxBounds(monitor, 420, 310),
            ViewMode = shared?.ViewMode ?? BoxViewMode.Grid,
            SortMode = shared?.SortMode ?? BoxSortMode.Name,
            Appearance = CloneAppearance(shared?.Appearance)
        };
        State.Boxes.Add(box);
        return box;
    }

    public async Task<DesktopBox> AddMappedFolderBoxAsync(string path, bool isReadOnly = false)
    {
        var normalizedPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        var monitor = Monitors.FirstOrDefault(candidate => candidate.IsPrimary) ?? Monitors.First();
        var shared = State.Boxes.FirstOrDefault();
        var box = new DesktopBox
        {
            Title = Path.GetFileName(normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            MonitorId = monitor.Id,
            Bounds = FindAvailableBoxBounds(monitor, 420, 310),
            ViewMode = shared?.ViewMode ?? BoxViewMode.Grid,
            SortMode = shared?.SortMode ?? BoxSortMode.Name,
            Appearance = CloneAppearance(shared?.Appearance),
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
        State.Assignments[itemKey] = boxId;
        MoveItemOrderKey(itemKey, boxId);
        ParkAssignedDesktopItems("assign item");
        NotifyWorkspaceChanged(true);
    }

    public void UnassignItem(string itemKey)
    {
        UnassignItemCore(itemKey);
        NotifyWorkspaceChanged(true);
    }

    public void UnassignItems(IEnumerable<string> itemKeys)
    {
        var changed = false;
        foreach (var itemKey in itemKeys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!State.Assignments.ContainsKey(itemKey))
            {
                continue;
            }
            UnassignItemCore(itemKey);
            changed = true;
        }
        if (changed)
        {
            NotifyWorkspaceChanged(true);
        }
    }

    // Assigned items are parked outside the visible desktop work area while
    // their original Explorer coordinates are kept. Unassigning restores the
    // native icon to its original place instead of leaving it behind a box or
    // at a stale captured position, which users experienced as "icons
    // disappearing".
    public int ParkAssignedDesktopItems(string context)
    {
        if (_disposed || IsPaused || _desktopHost.DesktopListView == IntPtr.Zero)
        {
            return 0;
        }
        var assignedItems = GetAssignedDesktopItems();
        if (assignedItems.Count == 0)
        {
            RestoreDesktopWorkAreas(true);
            return 0;
        }
        CaptureOriginalIconPositions(assignedItems);
        if (!EnsureExtendedParkingWorkArea())
        {
            DiagnosticLog.Info($"Assigned desktop icon parking work area unavailable context={context}");
            return 0;
        }
        var listViewBounds = DesktopWindowTools.GetWindowBounds(_desktopHost.DesktopListView);
        var parkingX = (int)Math.Round(listViewBounds.X + listViewBounds.Width + 4096);
        var parkingY = (int)Math.Round(listViewBounds.Y + listViewBounds.Height + 4096);
        var placements = assignedItems.Select(item => new DesktopIconPlacement(
            GetExplorerNames(item).ToArray(),
            parkingX,
            parkingY)).ToArray();
        var moved = DesktopIconPositionService.MoveItemsUnderBox(
            _desktopHost.DesktopListView,
            placements);
        DiagnosticLog.Info(
            $"Assigned desktop icons parked context={context} requested={placements.Length} moved={moved} " +
            $"items={string.Join(",", assignedItems.Select(item => item.DisplayName))}");
        return moved;
    }

    private bool EnsureExtendedParkingWorkArea()
    {
        var listView = _desktopHost.DesktopListView;
        if (listView == IntPtr.Zero)
        {
            return false;
        }
        if (_parkingWorkAreaListView != listView)
        {
            _parkingWorkAreaListView = listView;
            _originalDesktopWorkAreas = DesktopIconPositionService.GetWorkAreas(listView);
        }

        var bounds = DesktopWindowTools.GetWindowBounds(listView);
        var extended = new System.Drawing.Rectangle(
            0,
            0,
            Math.Max(1, (int)Math.Ceiling(bounds.Width) + 8192),
            Math.Max(1, (int)Math.Ceiling(bounds.Height) + 8192));
        return DesktopIconPositionService.SetWorkAreas(listView, [extended]);
    }

    private bool RestoreDesktopWorkAreas(bool clear)
    {
        if (_originalDesktopWorkAreas is null)
        {
            return true;
        }
        var restored = DesktopIconPositionService.SetWorkAreas(
            _parkingWorkAreaListView,
            _originalDesktopWorkAreas);
        if (restored && clear)
        {
            _originalDesktopWorkAreas = null;
            _parkingWorkAreaListView = IntPtr.Zero;
        }
        return restored;
    }

    private bool AssignedDesktopItemsNeedParking()
    {
        if (_disposed || IsPaused || _desktopHost.DesktopListView == IntPtr.Zero)
        {
            return false;
        }
        var assignedItems = GetAssignedDesktopItems();
        if (assignedItems.Count == 0)
        {
            return false;
        }
        var positions = DesktopIconPositionService.CaptureItemPositions(
            _desktopHost.DesktopListView,
            assignedItems.SelectMany(GetExplorerNames));
        var bounds = DesktopWindowTools.GetWindowBounds(_desktopHost.DesktopListView);
        var visible = positions.Count(position =>
            position.X >= 0 && position.X < bounds.Width &&
            position.Y >= 0 && position.Y < bounds.Height);
        var needsParking = positions.Count < assignedItems.Count || visible > 0;
        if (needsParking)
        {
            DiagnosticLog.Info(
                $"Assigned desktop icon parking drift detected assigned={assignedItems.Count} captured={positions.Count} visible={visible}");
        }
        return needsParking;
    }

    private int CaptureOriginalIconPositions(IEnumerable<DesktopItemRef> items)
    {
        if (_desktopHost.DesktopListView == IntPtr.Zero)
        {
            return 0;
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
            return 0;
        }
        var captured = DesktopIconPositionService.CaptureItemPositions(
            _desktopHost.DesktopListView,
            uncaptured.SelectMany(entry => entry.Names));
        if (captured.Count == 0)
        {
            var lvItems = DesktopIconPositionService
                .CaptureAllItemPositions(_desktopHost.DesktopListView)
                .Select(position => position.DisplayName);
            DiagnosticLog.Info(
                $"Assigned icon positions not found for names={string.Join(",", uncaptured.SelectMany(entry => entry.Names))} " +
                $"lvItems={string.Join(",", lvItems)}");
        }
        var positionsByName = captured
            .GroupBy(position => NormalizeExplorerName(position.DisplayName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new Queue<DesktopIconPositionSnapshot>(group),
                StringComparer.OrdinalIgnoreCase);
        var capturedCount = 0;
        foreach (var entry in uncaptured)
        {
            DesktopIconPositionSnapshot? position = null;
            foreach (var name in entry.Names)
            {
                if (positionsByName.TryGetValue(name, out var queue) && queue.Count > 0)
                {
                    position = queue.Dequeue();
                    break;
                }
            }
            if (position is not { } capturedPosition)
            {
                continue;
            }
            _originalIconPositions[entry.Item.Key.ToString()] = capturedPosition;
            capturedCount++;
        }
        return capturedCount;
    }

    private bool RestoreOriginalIconPositions(bool clear)
    {
        if (_originalIconPositions.Count == 0)
        {
            return true;
        }
        if (_desktopHost.DesktopListView == IntPtr.Zero)
        {
            return false;
        }
        var restored = DesktopIconPositionService.RestoreItemPositions(
            _desktopHost.DesktopListView,
            _originalIconPositions.Values);
        var complete = restored >= _originalIconPositions.Count;
        if (clear && complete)
        {
            _originalIconPositions.Clear();
        }
        return complete;
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

    public void BoxChanged(DesktopBox box, bool rebuild = false)
    {
        var monitor = Monitors.FirstOrDefault(candidate => candidate.Id == box.MonitorId)
            ?? Monitors.FirstOrDefault(candidate => candidate.IsPrimary)
            ?? Monitors.First();
        var minimumWidth = DesktopItemLayoutEngine.GetMinimumBoxWidth(
            box.ViewMode,
            box.Appearance.IconSize,
            State.Settings.Appearance.IconHorizontalSpacing);
        box.Bounds = box.Bounds.Clamp(
            new LayoutRect(0, 0, monitor.WorkArea.Width, monitor.WorkArea.Height),
            minimumWidth);
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
            State.Assignments[item.Key.ToString()] = boxId;
            MoveItemOrderKey(item.Key.ToString(), boxId);
        }
        ParkAssignedDesktopItems("import files");
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

    public async Task RefreshItemsAsync(bool applyDesktopRules = true)
    {
        var items = await _itemProvider.EnumerateAsync();
        // A failed or degraded enumeration (Explorer restart, cloud placeholder
        // lock, permission transition) must not wipe the persisted grouping or
        // make every desktop item disappear. Keep the previous snapshot when
        // the new one is empty and the desktop is known to have items.
        if (items.Count == 0 && _allDesktopItems.Count > 0)
        {
            DiagnosticLog.Info("Desktop enumeration returned no items; keeping previous snapshot.");
            items = _allDesktopItems;
        }
        _allDesktopItems = items;
        Items = State.Settings.ShowSystemItems
            ? items
            : items.Where(item => !item.IsSystem || State.Assignments.ContainsKey(item.Key.ToString())).ToArray();
        await RefreshMappedFoldersAsync(false);
        if (applyDesktopRules && State.Organization.Enabled && State.Organization.RunOnDesktopChanges)
        {
            ApplyOrganizationRules(false);
        }
        _surfaceManager?.Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    private void RepairHiddenAssignedItemAttributes()
    {
        // A legacy mechanism (removed) hid assigned desktop items by
        // stamping Hidden+System attributes on them. Explorer hides such
        // items, which users experienced as "icons disappearing" while the
        // box still listed the file. Restore those attributes so the icons
        // reappear; the current design parks icons instead of hiding files.
        var repaired = new List<string>();
        foreach (var item in GetAssignedDesktopItems())
        {
            if (item.FileSystemPath is not { } path)
            {
                continue;
            }
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) ==
                    (FileAttributes.Hidden | FileAttributes.System))
                {
                    File.SetAttributes(path, attributes & ~(FileAttributes.Hidden | FileAttributes.System));
                    repaired.Add(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        if (repaired.Count > 0)
        {
            _desktopHost.NotifyItemAttributesChanged(repaired);
            DiagnosticLog.Info($"Restored hidden attributes on {repaired.Count} assigned desktop items");
        }
    }

    public void SetPaused(bool paused)
    {
        if (paused == IsPaused)
        {
            return;
        }

        IsPaused = paused;
        DiagnosticLog.Info($"SetPaused paused={paused}");
        State.Settings.TakeOverDesktop = !paused;
        if (paused)
        {
            AreDesktopItemsHidden = false;
            RestoreDesktopWorkAreas(true);
            RestoreOriginalIconPositions(true);
            if (_desktopInputMonitor is not null)
            {
                _desktopInputMonitor.Enabled = false;
            }
            try
            {
                _surfaceManager?.Dispose();
            }
            finally
            {
                _surfaceManager = null;
                EnsureDesktopInput("pause");
            }
        }
        else
        {
            ConfigureDesktopInputMonitor();
            AreDesktopItemsHidden = false;
            ActivateDesktopSurfaces("resume");
            ParkAssignedDesktopItems("resume");
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
        if (ShouldSkipUpdateCheck(manual))
        {
            return LastUpdateCheck;
        }
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
                settings.CachedIsPrerelease,
                UpdateConfiguration.InstallerAssetName);
            var result = await _updateService.CheckAsync(request, _updateCancellation.Token);
            if (string.IsNullOrWhiteSpace(result.ReleasePageUrl))
            {
                result = result with { ReleasePageUrl = GetReleasePageUrl() };
            }
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

    public async Task<UpdateDownloadResult> DownloadUpdateAsync(
        IProgress<UpdateDownloadProgress>? progress = null)
    {
        if (!await _updateLock.WaitAsync(0))
        {
            return new UpdateDownloadResult(false, Message: "另一个更新操作正在进行");
        }
        IsDownloadingUpdate = true;
        _verifiedUpdateInstallerPath = null;
        _verifiedUpdateSha256 = null;
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            var update = LastUpdateCheck;
            if (update.Status != UpdateCheckStatus.UpdateAvailable)
            {
                return new UpdateDownloadResult(false, Message: "当前没有可下载的新版本");
            }
            if (string.IsNullOrWhiteSpace(update.InstallerUrl) ||
                string.IsNullOrWhiteSpace(update.Sha256Url))
            {
                return new UpdateDownloadResult(false, Message: "该版本缺少安装包或 SHA256SUMS.txt");
            }

            var request = new UpdateDownloadRequest(
                update.InstallerUrl,
                update.Sha256Url,
                update.LatestVersion,
                Path.Combine(ConfigDirectory, "Updates"),
                update.InstallerAssetName);
            var downloaded = await _updateService.DownloadAsync(
                request,
                progress,
                _updateCancellation.Token);
            if (!downloaded.Success)
            {
                return downloaded with { IsPrerelease = update.IsPrerelease };
            }

            progress?.Report(new UpdateDownloadProgress("正在验证安装包签名"));
            var signature = AuthenticodeVerifier.Verify(downloaded.InstallerPath);
            if (!update.IsPrerelease && !signature.IsTrusted)
            {
                File.Delete(downloaded.InstallerPath);
                return downloaded with
                {
                    Success = false,
                    IsPrerelease = false,
                    Message = $"稳定版安装包签名验证失败：{signature.Message}"
                };
            }
            if (!update.IsPrerelease &&
                signature.IsTrusted &&
                Environment.ProcessPath is { } currentExecutable)
            {
                var currentSignature = AuthenticodeVerifier.Verify(currentExecutable);
                if (currentSignature.IsTrusted &&
                    !string.Equals(
                        currentSignature.SignerSubject,
                        signature.SignerSubject,
                        StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(downloaded.InstallerPath);
                    return downloaded with
                    {
                        Success = false,
                        Message = "更新安装包与当前 CrabDesk 的签名发布者不一致"
                    };
                }
            }

            _verifiedUpdateInstallerPath = Path.GetFullPath(downloaded.InstallerPath);
            _verifiedUpdateSha256 = downloaded.Sha256;
            _verifiedUpdateIsPrerelease = update.IsPrerelease;
            return downloaded with
            {
                SignatureTrusted = signature.IsTrusted,
                SignerSubject = signature.SignerSubject,
                IsPrerelease = update.IsPrerelease,
                Message = signature.IsTrusted
                    ? $"下载完成，签名发布者：{signature.SignerSubject}"
                    : $"下载完成；该预览版未通过签名验证：{signature.Message}"
            };
        }
        catch (OperationCanceledException)
        {
            return new UpdateDownloadResult(false, Message: "更新下载已取消");
        }
        finally
        {
            IsDownloadingUpdate = false;
            _updateLock.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void LaunchUpdateInstaller(string installerPath)
    {
        var fullPath = Path.GetFullPath(installerPath);
        var updateRoot = Path.GetFullPath(Path.Combine(ConfigDirectory, "Updates"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (_verifiedUpdateInstallerPath is null ||
            !fullPath.Equals(_verifiedUpdateInstallerPath, StringComparison.OrdinalIgnoreCase) ||
            !fullPath.StartsWith(updateRoot, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fullPath) ||
            string.IsNullOrWhiteSpace(_verifiedUpdateSha256))
        {
            throw new InvalidOperationException("更新安装包尚未完成验证。");
        }

        using (var stream = File.OpenRead(fullPath))
        {
            var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(_verifiedUpdateSha256),
                    Convert.FromHexString(actualHash)))
            {
                throw new InvalidDataException("安装包在下载后发生变化，已阻止启动。");
            }
        }
        if (!_verifiedUpdateIsPrerelease)
        {
            var signature = AuthenticodeVerifier.Verify(fullPath);
            if (!signature.IsTrusted)
            {
                throw new InvalidDataException($"安装包签名再次验证失败：{signature.Message}");
            }
        }

        _ = Process.Start(new ProcessStartInfo(fullPath)
        {
            UseShellExecute = true,
            Arguments = "/CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /NORESTART"
        }) ?? throw new InvalidOperationException("安装程序没有成功启动。");
        RequestExit();
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

    private string GetReleasePageUrl()
    {
        var repository = UpdateConfiguration.ResolveRepository(State.Settings.Updates);
        if (string.IsNullOrWhiteSpace(repository.Owner) || string.IsNullOrWhiteSpace(repository.Repository))
        {
            return string.Empty;
        }

        return $"https://github.com/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Repository)}/releases";
    }

    private bool ShouldSkipUpdateCheck(bool manual)
    {
        var settings = State.Settings.Updates;
        if (settings.LastCheckedAt is not { } checkedAt)
        {
            return false;
        }

        var age = DateTimeOffset.Now - checkedAt;
        if (age < TimeSpan.Zero)
        {
            return false;
        }

        if (settings.LastStatus == UpdateCheckStatus.RateLimited)
        {
            return age < TimeSpan.FromHours(1);
        }

        return !manual && age < TimeSpan.FromHours(6);
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
        // Keep the legacy service entry point compatible while storing the
        // actual preference per box. New UI actions use the title-bar toggle.
        foreach (var box in State.Boxes)
        {
            box.ExpandOnHover = enabled;
        }
        State.Settings.DesktopBehavior.ExpandBoxOnHover = false;
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
        LayoutCoordinator.NormalizeForMonitors(State, Monitors);
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

    public void SetBoxTitleFontFamily(Guid? boxId, string value)
    {
        var family = NormalizeFontFamily(value);
        foreach (var box in GetAppearanceTargets(boxId))
        {
            box.Appearance.TitleFontFamily = family;
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
        var backup = await service.CreateAsync(State, CaptureDesktopBackup());
        State.Settings.Backup.LastBackupAt = DateTimeOffset.Now;
        await service.CleanupAsync(State.Settings.Backup.RetentionDays);
        ScheduleSave();
        Changed?.Invoke(this, EventArgs.Empty);
        return backup;
    }

    public async Task<LayoutResetResult> ResetLayoutAsync()
    {
        var backup = await CreateBackupAsync();
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

    public Task ExportBackupAsync(string path) =>
        GetBackupService().ExportAsync(State, path, CaptureDesktopBackup());

    public async Task RestoreBackupAsync(string path)
    {
        var service = GetBackupService();
        await service.CreateAsync(State, CaptureDesktopBackup());
        var imported = await service.LoadDocumentAsync(path);
        var previous = State;
        try
        {
            await ApplyLoadedStateAsync(imported.State);
            RestoreDesktopBackup(imported.Snapshot);
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

    public void SetBackupDirectory(string path)
    {
        var normalized = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
        Directory.CreateDirectory(normalized);
        if (string.Equals(State.Settings.Backup.BackupDirectory, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        State.Settings.Backup.BackupDirectory = normalized;
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public void SetDailyBackup(bool enabled)
    {
        State.Settings.Backup.DailyBackup = enabled;
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public void SetBackupIntervalHours(int hours)
    {
        State.Settings.Backup.IntervalHours = Math.Clamp(hours, 1, 8760);
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

    public void ConfigureAiClassification(
        string baseUrl,
        string apiKey,
        string model,
        string categoryLabels,
        string customPrompt,
        bool reassignExistingItems)
    {
        var settings = State.Settings.AiClassification;
        settings.BaseUrl = baseUrl?.Trim() ?? string.Empty;
        settings.ApiKey = apiKey ?? string.Empty;
        settings.Model = model?.Trim() ?? string.Empty;
        settings.CategoryLabels = categoryLabels ?? string.Empty;
        settings.CustomPrompt = customPrompt ?? string.Empty;
        settings.ReassignExistingItems = reassignExistingItems;
        try
        {
            AiApiKeyStore.Save(GetAiApiKeyPath(), settings.ApiKey);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Failed to save encrypted AI API key", exception);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public Task<IReadOnlyList<string>> GetAiModelsAsync(CancellationToken cancellationToken = default) =>
        _aiClassificationService.GetModelsAsync(State.Settings.AiClassification, cancellationToken);

    public Task TestAiModelConnectivityAsync(CancellationToken cancellationToken = default) =>
        _aiClassificationService.TestModelConnectivityAsync(State.Settings.AiClassification, cancellationToken);

    public async Task<AiClassificationApplyResult> ApplyAiClassificationAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = State.Settings.AiClassification;
        var labels = ParseAiCategoryLabels(settings.CategoryLabels);
        if (labels.Count == 0)
        {
            throw new InvalidOperationException("请至少提供一个分类标签。");
        }
        var candidates = Items
            .Where(item => settings.ReassignExistingItems || !State.Assignments.ContainsKey(item.Key.ToString()))
            .Select(item => new AiClassificationInput(item.Key.ToString(), item.DisplayName))
            .ToArray();
        if (candidates.Length == 0)
        {
            return new AiClassificationApplyResult(0, 0, 0, 0, 0, []);
        }

        var classifications = await _aiClassificationService.ClassifyAsync(
            settings,
            candidates,
            labels,
            cancellationToken);
        if (classifications.Count == 0)
        {
            return new AiClassificationApplyResult(candidates.Length, 0, 0, 0, candidates.Length, []);
        }

        _lastOrganizationAssignments = new Dictionary<string, Guid>(
            State.Assignments,
            StringComparer.OrdinalIgnoreCase);
        var boxesByTitle = State.Boxes
            .Where(box => !box.IsMappedFolder)
            .GroupBy(box => box.Title.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var createdBoxes = 0;
        var applied = 0;
        foreach (var classification in classifications)
        {
            if (!boxesByTitle.TryGetValue(classification.Label, out var box))
            {
                box = CreateBoxCore(classification.Label);
                boxesByTitle[classification.Label] = box;
                createdBoxes++;
            }
            var item = Items.FirstOrDefault(candidate => string.Equals(
                candidate.Key.ToString(),
                classification.ItemKey,
                StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                continue;
            }
            State.Assignments[classification.ItemKey] = box.Id;
            MoveItemOrderKey(classification.ItemKey, box.Id);
            applied++;
        }
        if (applied > 0 || createdBoxes > 0)
        {
            ParkAssignedDesktopItems("ai classification");
            if (IsPaused)
            {
                SetPaused(false);
            }
            else
            {
                NotifyWorkspaceChanged(true);
            }
        }
        return new AiClassificationApplyResult(
            candidates.Length,
            classifications.Count,
            applied,
            createdBoxes,
            candidates.Length - classifications.Count,
            classifications);
    }

    private static IReadOnlyList<string> ParseAiCategoryLabels(string value) => value
        .Split(['\r', '\n', ',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(label => label.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private string GetAiApiKeyPath() => Path.Combine(ConfigDirectory, "ai-api-key.dat");

    public OrganizationApplyResult ApplyOrganizationRules(bool notify = true)
    {
        EnsureSmartOrganizationStructure();
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
        if (assigned > 0 || unassigned > 0)
        {
            ParkAssignedDesktopItems("organization");
        }
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

    // Rule-driven assignment parks each newly assigned item's native
    // Explorer icon outside the visible desktop so the desktop does not
    // show a duplicate of the same item.
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
        if (IsPaused)
        {
            SetPaused(false);
        }
        else
        {
            NotifyWorkspaceChanged(true);
        }
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
        var validBoxes = State.Boxes.Where(box => !box.IsMappedFolder).Select(box => box.Id).ToHashSet();
        foreach (var (key, target) in previous)
        {
            if (State.Assignments.TryGetValue(key, out var current) && current == target)
            {
                continue;
            }
            if (!validBoxes.Contains(target))
            {
                // The target box was deleted since the organization run.
                // Restore the item to the desktop instead of parking it in a
                // box that no longer exists, where it would be unreachable.
                UnassignItemCore(key);
                continue;
            }
            State.Assignments[key] = target;
            MoveItemOrderKey(key, target);
        }
        NotifyWorkspaceChanged(true);
    }

    public void InstallDefaultOrganizationRules()
    {
        BuiltInOrganizationRules.EnsureRules(State);
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

        existing.BuiltInId = editedRule.BuiltInId?.Trim() ?? string.Empty;
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
        copy.BuiltInId = string.Empty;
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
        if (!OrganizationRuleOrdering.Move(State.OrganizationRules, ruleId, direction))
        {
            return;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public void SetBoxIconSize(Guid? boxId, double value)
    {
        foreach (var box in GetAppearanceTargets(boxId))
        {
            box.Appearance.IconSize = Math.Clamp(value, 24, 96);
        }
        LayoutCoordinator.NormalizeForMonitors(State, Monitors);
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

    public void SetBoxLabelFontFamily(Guid? boxId, string value)
    {
        var family = NormalizeFontFamily(value);
        foreach (var box in GetAppearanceTargets(boxId))
        {
            box.Appearance.LabelFontFamily = family;
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
        LayoutCoordinator.NormalizeForMonitors(State, Monitors);
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

    public void SetWindowBackdrop(string backdrop)
    {
        var normalized = backdrop?.Trim() switch
        {
            "MicaAlt" => "MicaAlt",
            "Acrylic" => "Acrylic",
            _ => "Mica"
        };
        if (string.Equals(State.Settings.WindowBackdrop, normalized, StringComparison.Ordinal))
        {
            return;
        }

        State.Settings.WindowBackdrop = normalized;
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public async Task ReconnectDesktopAsync()
    {
        _desktopHost.Refresh();
        if (_desktopInputMonitor is not null)
        {
            _desktopInputMonitor.DesktopListView = _desktopHost.DesktopListView;
        }
        Monitors = _monitorService.GetMonitors();
        LayoutCoordinator.NormalizeForMonitors(State, Monitors);
        if (!IsPaused)
        {
            ActivateDesktopSurfaces("reconnect");
        }
        await RefreshItemsAsync(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> RepairDesktopIconsAsync()
    {
        if (_disposed)
        {
            return false;
        }

        DiagnosticLog.Info($"Explorer shell repair started paused={IsPaused}");
        _desktopHost.Refresh();
        if (!_desktopHost.IsAvailable ||
            _desktopHost.DesktopView == IntPtr.Zero ||
            _desktopHost.DesktopListView == IntPtr.Zero)
        {
            DiagnosticLog.Info("Explorer shell repair skipped because the Explorer desktop view is unavailable");
            Changed?.Invoke(this, EventArgs.Empty);
            return false;
        }

        if (_desktopInputMonitor is not null)
        {
            _desktopInputMonitor.DesktopListView = _desktopHost.DesktopListView;
        }

        var resumeTakeover = !IsPaused;
        if (_desktopInputMonitor is not null)
        {
            _desktopInputMonitor.Enabled = false;
            _desktopInputMonitor.DesktopListView = IntPtr.Zero;
        }
        _surfaceManager?.Dispose();
        _surfaceManager = null;
        EnsureDesktopInput("desktop icon repair");

        _hostTimer.Stop();
        var explorerRestarted = false;
        try
        {
            explorerRestarted = await _desktopHost.RestartExplorerShellAsync();
        }
        finally
        {
            if (!_disposed)
            {
                _hostTimer.Start();
            }
        }
        if (!explorerRestarted)
        {
            DiagnosticLog.Info("Explorer shell repair did not restore a desktop view before timeout");
            Changed?.Invoke(this, EventArgs.Empty);
            return false;
        }

        EnsureDesktopInput("Explorer shell restart");
        Monitors = _monitorService.GetMonitors();
        NormalizeMonitorIds();
        LayoutCoordinator.NormalizeForMonitors(State, Monitors);
        _iconProvider.ClearCache();
        await RefreshItemsAsync(false);
        if (resumeTakeover && !await ActivateDesktopSurfacesAfterExplorerRestartAsync())
        {
            return false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        DiagnosticLog.Info("Explorer shell repair completed");
        return _desktopHost.GetIconImageListState().HasImageList;
    }

    public void RequestShowSettings(string? page = null) =>
        ShowSettingsRequested?.Invoke(this, new ShowSettingsRequestedEventArgs(page));
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
        DiagnosticLog.Info("Runtime disposal started");
        _hostTimer.Stop();
        _saveTimer.Stop();
        _desktopZoomTimer.Stop();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        try
        {
            _surfaceManager?.Dispose();
        }
        finally
        {
            _surfaceManager = null;
            RestoreDesktopWorkAreas(true);
            RestoreOriginalIconPositions(true);
            EnsureDesktopInput("dispose");
        }
        _itemProvider.Dispose();
        _mappedFolderProvider.Dispose();
        _hotkeyService.Pressed -= OnGlobalHotkeyPressed;
        _hotkeyService.Dispose();
        if (_desktopInputMonitor is not null)
        {
            _desktopInputMonitor.EmptyAreaClicked -= OnDesktopEmptyAreaClicked;
            _desktopInputMonitor.IconZoomRequested -= OnDesktopIconZoomRequested;
            _desktopInputMonitor.Dispose();
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
        _menuFont.Dispose();
        _aiClassificationService.Dispose();
        _iconProvider.ClearCache();
        SaveNowAsync().GetAwaiter().GetResult();
        _saveLock.Dispose();
        _mappedRefreshLock.Dispose();
        _desktopZoomTimer.Dispose();
        DiagnosticLog.Info("Runtime disposal completed");
    }

    private void StartTakeover()
    {
        DiagnosticLog.Info($"Desktop takeover starting listView=0x{_desktopHost.DesktopListView.ToInt64():X} items={_allDesktopItems.Count}");
        IsPaused = false;
        AreDesktopItemsHidden = false;
        if (ActivateDesktopSurfaces("startup"))
        {
            DiagnosticLog.Info("Desktop takeover started");
        }
    }

    private void RebuildSurfaces()
    {
        DiagnosticLog.Info($"RebuildSurfaces hostAvailable={_desktopHost.IsAvailable} monitors={Monitors.Count}");
        _surfaceManager?.Dispose();
        _surfaceManager = null;
        if (!_desktopHost.IsAvailable || Monitors.Count == 0)
        {
            return;
        }
        try
        {
            _surfaceManager = new DesktopSurfaceManager(this, _desktopHost, Monitors);
        }
        finally
        {
            EnsureDesktopInput("surface rebuild");
        }
        DiagnosticLog.Info($"RebuildSurfaces completed surfaces={_surfaceManager.SurfaceCount}");
    }

    private bool ActivateDesktopSurfaces(string context)
    {
        try
        {
            if (!TryRebuildDesktopSurfaces())
            {
                throw new InvalidOperationException("No CrabDesk desktop surface was created.");
            }
            return true;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error($"Desktop takeover failed context={context}", exception);
            try
            {
                _surfaceManager?.Dispose();
            }
            catch (Exception disposeException)
            {
                DiagnosticLog.Error("Failed to dispose an unusable desktop surface", disposeException);
            }
            _surfaceManager = null;
            IsPaused = true;
            State.Settings.TakeOverDesktop = false;
            AreDesktopItemsHidden = false;
            EnsureDesktopInput($"takeover rollback ({context})");
            return false;
        }
    }

    private bool TryRebuildDesktopSurfaces()
    {
        RebuildSurfaces();
        if (_surfaceManager is null)
        {
            return false;
        }
        try
        {
            _surfaceManager.EnsureReady();
            _surfaceManager.SetVisible(!AreDesktopItemsHidden);
            if (!AreDesktopItemsHidden)
            {
                _surfaceManager.Refresh();
            }
            return true;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Desktop surface activation failed", exception);
            try
            {
                _surfaceManager?.Dispose();
            }
            catch (Exception disposeException)
            {
                DiagnosticLog.Error("Failed to dispose an unusable desktop surface", disposeException);
            }
            _surfaceManager = null;
            return false;
        }
    }

    // Explorer takes a moment to recreate its desktop view after the shell
    // restarts; retry the takeover instead of failing on the first transient
    // miss. Only roll back to the paused state after all attempts fail.
    private async Task<bool> ActivateDesktopSurfacesAfterExplorerRestartAsync()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (TryRebuildDesktopSurfaces())
            {
                ConfigureDesktopInputMonitor();
                return true;
            }
            await Task.Delay(500);
            _desktopHost.Refresh();
            Monitors = _monitorService.GetMonitors();
        }

        IsPaused = true;
        State.Settings.TakeOverDesktop = false;
        AreDesktopItemsHidden = false;
        EnsureDesktopInput("takeover rollback (Explorer shell restart)");
        return false;
    }

    private void EnsureDesktopInput(string context)
    {
        var wasEnabled = _desktopHost.IsDesktopInputEnabled;
        var isEnabled = _desktopHost.EnsureDesktopInputEnabled();
        if (!wasEnabled)
        {
            DiagnosticLog.Info(
                $"Desktop input recovery context={context} parent=0x{_desktopHost.DesktopParent.ToInt64():X} success={isEnabled}");
        }
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

    private static BoxAppearance CloneAppearance(BoxAppearance? source)
    {
        source ??= new BoxAppearance();
        return new BoxAppearance
        {
            Background = source.Background,
            Accent = source.Accent,
            Opacity = source.Opacity,
            IconSize = source.IconSize,
            LabelFontFamily = source.LabelFontFamily,
            LabelFontSize = source.LabelFontSize,
            ShowItemLabels = source.ShowItemLabels,
            TitleBarHeight = source.TitleBarHeight,
            TitleAlignment = source.TitleAlignment,
            TitleColor = source.TitleColor,
            TitleFontFamily = source.TitleFontFamily,
            TitleFontSize = source.TitleFontSize,
            TitleFontBold = source.TitleFontBold,
            ShowCollapseButton = source.ShowCollapseButton
        };
    }

    private void MigrateGlobalHoverExpansionSetting()
    {
        if (!State.Settings.DesktopBehavior.ExpandBoxOnHover)
        {
            return;
        }

        foreach (var box in State.Boxes)
        {
            box.ExpandOnHover = true;
        }

        State.Settings.DesktopBehavior.ExpandBoxOnHover = false;
        DiagnosticLog.Info($"Migrated legacy global hover expansion to {State.Boxes.Count} boxes");
        ScheduleSave();
    }

    private void SynchronizeBoxStyles()
    {
        if (State.Boxes.FirstOrDefault() is not { } source) return;
        foreach (var box in State.Boxes.Skip(1))
        {
            box.Appearance = CloneAppearance(source.Appearance);
        }
    }

    private void EnsureSmartOrganizationStructure()
    {
        var monitor = Monitors.FirstOrDefault(candidate => candidate.IsPrimary) ?? Monitors.First();
        var active = new List<(DesktopBox Box, int ItemCount)>();
        var createdBoxIds = new HashSet<Guid>();
        foreach (var definition in BuiltInOrganizationRules.Definitions)
        {
            var rule = State.OrganizationRules.FirstOrDefault(candidate =>
                string.Equals(candidate.BuiltInId, definition.Id, StringComparison.OrdinalIgnoreCase));
            if (rule is null)
            {
                continue;
            }
            var matchingItems = rule.Enabled && rule.Action == OrganizationRuleAction.AssignToBox
                ? Items.Where(item => OrganizationRuleEngine.MatchesRule(rule, item)).ToArray()
                : [];
            var box = rule.TargetBoxId is { } target
                ? State.Boxes.FirstOrDefault(candidate => candidate.Id == target && !candidate.IsMappedFolder)
                : State.Boxes.FirstOrDefault(candidate => candidate.IsAutoGenerated &&
                    string.Equals(candidate.Title, rule.Title, StringComparison.CurrentCultureIgnoreCase));

            if (matchingItems.Length == 0)
            {
                // A user rule can reuse this auto-generated box (matching
                // titles); removing it would orphan that rule's target and
                // every future decision would count as an invalid target.
                var referencedByAnotherRule = State.OrganizationRules.Any(candidate =>
                    candidate.Id != rule.Id &&
                    candidate.Enabled &&
                    candidate.Action == OrganizationRuleAction.AssignToBox &&
                    candidate.TargetBoxId == box?.Id);
                if (box is not null &&
                    !referencedByAnotherRule &&
                    (box.IsAutoGenerated || rule.TargetBoxId == box.Id) &&
                    !State.Assignments.Values.Contains(box.Id))
                {
                    State.Boxes.Remove(box);
                    rule.TargetBoxId = null;
                }
                continue;
            }

            if (box is null)
            {
                box = new DesktopBox
                {
                    Title = rule.Title,
                    MonitorId = monitor.Id,
                    IsAutoGenerated = true,
                    Appearance = CloneAppearance(State.Boxes.FirstOrDefault()?.Appearance)
                };
                State.Boxes.Add(box);
                createdBoxIds.Add(box.Id);
            }
            if (box.IsAutoGenerated)
            {
                box.Title = rule.Title;
                if (createdBoxIds.Contains(box.Id))
                {
                    box.MonitorId = monitor.Id;
                }
            }

            rule.TargetBoxId = box.Id;
            if (box.IsAutoGenerated)
            {
                active.Add((box, matchingItems.Length));
            }
        }

        foreach (var rule in State.OrganizationRules.Where(rule =>
                     rule.Enabled && rule.Action == OrganizationRuleAction.AssignToBox &&
                     string.IsNullOrWhiteSpace(rule.BuiltInId)).ToArray())
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
                    createdBoxIds.Add(box.Id);
                }
            }
            // Always pin the rule to its box (created or reused) so preview
            // decisions carry a valid target.
            rule.TargetBoxId = box.Id;
            if (box.IsAutoGenerated && active.All(entry => entry.Box.Id != box.Id))
            {
                if (createdBoxIds.Contains(box.Id))
                {
                    box.MonitorId = monitor.Id;
                }
                active.Add((box, matchingItems.Length));
            }
        }

        var activeIds = active.Select(entry => entry.Box.Id).ToHashSet();
        var occupied = State.Boxes
            .Where(box => string.Equals(box.MonitorId, monitor.Id, StringComparison.OrdinalIgnoreCase) &&
                !activeIds.Contains(box.Id))
            .Select(box => box.Bounds)
            .ToArray();
        PlaceAutoGeneratedBoxes(active, createdBoxIds, monitor.Id, monitor.WorkArea, occupied);
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

    // Auto-generated boxes keep the position the user last left them in.
    // Only boxes that are new this run (or whose bounds are degenerate) are
    // arranged into free space, so an organization pass never resets a box
    // the user has already moved or resized.
    internal static void PlaceAutoGeneratedBoxes(
        IReadOnlyList<(DesktopBox Box, int ItemCount)> active,
        IReadOnlySet<Guid> createdBoxIds,
        string monitorId,
        LayoutRect workArea,
        IReadOnlyList<LayoutRect> occupied)
    {
        var existingBoxes = active
            .Where(entry => !createdBoxIds.Contains(entry.Box.Id) &&
                entry.Box.Bounds.Width > 0 &&
                entry.Box.Bounds.Height > 0)
            .ToArray();
        var newBoxes = active
            .Where(entry => createdBoxIds.Contains(entry.Box.Id) ||
                entry.Box.Bounds.Width <= 0 ||
                entry.Box.Bounds.Height <= 0)
            .ToArray();
        var requested = newBoxes.Select(entry => new LayoutRect(
            0,
            0,
            360,
            Math.Clamp(82 + Math.Ceiling(entry.ItemCount / 4d) * 88, 190, 366))).ToArray();
        var arranged = BoxLayoutPlanner.Arrange(
            workArea,
            requested,
            occupied.Concat(existingBoxes.Select(entry => entry.Box.Bounds)).ToArray());
        for (var index = 0; index < newBoxes.Length; index++)
        {
            newBoxes[index].Box.Bounds = arranged[index];
            newBoxes[index].Box.MonitorId = monitorId;
        }
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
        BuiltInId = source.BuiltInId,
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

    private static string NormalizeFontFamily(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Segoe UI" : value.Trim();

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
            if (hostChanged)
            {
                DiagnosticLog.Info(
                    $"Host refresh detected parent=0x{_desktopHost.DesktopParent.ToInt64():X} " +
                    $"view=0x{_desktopHost.DesktopView.ToInt64():X} " +
                    $"listView=0x{_desktopHost.DesktopListView.ToInt64():X}");
                EnsureDesktopInput("host refresh");
            }
            if (_desktopInputMonitor is not null)
            {
                _desktopInputMonitor.DesktopListView = _desktopHost.DesktopListView;
            }
            // No periodic icon operations: every touch of Explorer's desktop
            // ListView from this timer proved to be a source of icon loss.
            // Icons are parked/restored only on explicit actions, and the
            // user can repair the desktop with the dedicated button.
            var monitors = _monitorService.GetMonitors();
            var topologyChanged = !monitors.Select(monitor => $"{monitor.Id}:{monitor.PixelBounds}")
                .SequenceEqual(Monitors.Select(monitor => $"{monitor.Id}:{monitor.PixelBounds}"));
            if (topologyChanged)
            {
                DiagnosticLog.Info(
                    $"Monitor topology changed new={monitors.Count} " +
                    $"old={Monitors.Count} " +
                    string.Join("|", monitors.Select(monitor => $"{monitor.Id}:{monitor.PixelBounds}:{monitor.PixelWorkArea}")));
            }
            if (!hostChanged && !topologyChanged)
            {
                // This timer only checks HWND/topology health. Do not touch
                // Explorer's shared icon cache from a periodic callback.
                return;
            }

            Monitors = monitors;
            NormalizeMonitorIds();
            LayoutCoordinator.NormalizeForMonitors(State, Monitors);
            if (!IsPaused)
            {
                ActivateDesktopSurfaces("host refresh");
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Host timer failed", exception);
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
        _beginInvoke(() =>
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

    private void ConfigureDesktopInputMonitor()
    {
        if (_desktopInputMonitor is null)
        {
            _desktopInputMonitor = new DesktopInputMonitor();
            _desktopInputMonitor.EmptyAreaClicked += OnDesktopEmptyAreaClicked;
            _desktopInputMonitor.IconZoomRequested += OnDesktopIconZoomRequested;
        }
        _desktopInputMonitor.DesktopListView = _desktopHost.DesktopListView;
        _desktopInputMonitor.Enabled = true;
    }

    private void OnDesktopEmptyAreaClicked(object? sender, EventArgs eventArgs)
    {
        _beginInvoke(() => _surfaceManager?.ClearSelection());
    }

    private void OnDesktopIconZoomRequested(object? sender, DesktopIconZoomEventArgs eventArgs)
    {
        _desktopZoomTimer.Start();
    }

    private void SynchronizeDesktopIconZoom()
    {
        if (_disposed || DesktopIconPositionService.GetDesktopIconSize() is not { } nativeIconSize)
        {
            return;
        }

        var iconSize = Math.Clamp(nativeIconSize, 24, 96);
        foreach (var box in State.Boxes)
        {
            box.Appearance.IconSize = iconSize;
        }
        var spacing = DesktopIconPositionService.GetItemSpacing(_desktopHost.DesktopListView);
        DiagnosticLog.Info(
            $"Desktop icon zoom synchronized size={iconSize} spacing={spacing.Width}x{spacing.Height}");
        NotifyWorkspaceChanged(true);
    }

    private void OnSystemPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs eventArgs)
    {
        if (State.Settings.ThemeMode != ApplicationThemeMode.System || _disposed)
        {
            return;
        }

        _beginInvoke(() => ApplyTheme(true));
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs eventArgs)
    {
        if (_disposed)
        {
            return;
        }
        if (eventArgs.Mode == Microsoft.Win32.PowerModes.Suspend)
        {
            _beginInvoke(async () =>
            {
                if (_disposed)
                {
                    return;
                }
                await SaveNowAsync();
            });
            return;
        }
        if (eventArgs.Mode != Microsoft.Win32.PowerModes.Resume)
        {
            return;
        }

        _beginInvoke(async () =>
        {
            await Task.Delay(1200);
            if (_disposed)
            {
                return;
            }
            await ReconnectDesktopAsync();
        });
    }

    private async Task RunScheduledBackupIfNeededAsync()
    {
        var settings = State.Settings.Backup;
        if (!settings.DailyBackup)
        {
            return;
        }

        var interval = TimeSpan.FromHours(Math.Clamp(settings.IntervalHours, 1, 8760));
        if (settings.LastBackupAt is { } lastBackupAt &&
            DateTimeOffset.Now - lastBackupAt < interval)
        {
            return;
        }

        var service = GetBackupService();
        await service.CreateAsync(State, CaptureDesktopBackup());
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

    private DesktopBackupCapture CaptureDesktopBackup()
    {
        var positions = DesktopIconPositionService
            .CaptureAllItemPositions(_desktopHost.DesktopListView)
            .ToArray();
        return new DesktopBackupCapture(
            positions,
            DesktopWallpaperService.GetCurrentWallpaperPath());
    }

    private void RestoreDesktopBackup(LayoutBackupSnapshot snapshot)
    {
        if (snapshot.IconPositions is { Count: > 0 })
        {
            _desktopHost.Refresh();
            DesktopIconPositionService.RestoreItemPositions(
                _desktopHost.DesktopListView,
                snapshot.IconPositions);
        }
        if (!string.IsNullOrWhiteSpace(snapshot.WallpaperPath))
        {
            DesktopWallpaperService.SetWallpaper(snapshot.WallpaperPath);
        }
    }

    private async Task ApplyLoadedStateAsync(CrabDeskState state)
    {
        var localAiApiKey = State.Settings.AiClassification.ApiKey;
        try
        {
            _surfaceManager?.Dispose();
        }
        finally
        {
            _surfaceManager = null;
            EnsureDesktopInput("state reload");
        }
        State = state;
        State.Settings.AiClassification.ApiKey = localAiApiKey;
        SynchronizeBoxStyles();
        _lastOrganizationAssignments = null;
        LastUpdateCheck = new UpdateCheckResult(
            UpdateCheckStatus.NotChecked,
            CurrentVersion,
            ReleasePageUrl: GetReleasePageUrl());
        AreDesktopItemsHidden = false;
        StartupRegistration.SetEnabled(State.Settings.StartWithWindows);
        ApplyHotkeys();
        _desktopContextMenu.SetEnabled(
            true,
            Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CrabDesk.WinUI.exe"));
        if (State.Settings.TakeOverDesktop)
        {
            ConfigureDesktopInputMonitor();
        }
        else if (_desktopInputMonitor is not null)
        {
            _desktopInputMonitor.Enabled = false;
        }
        ApplyTheme(false);
        Monitors = _monitorService.GetMonitors();
        NormalizeMonitorIds();
        LayoutCoordinator.NormalizeForMonitors(State, Monitors);
        await RefreshItemsAsync(false);

        IsPaused = !State.Settings.TakeOverDesktop;
        if (!IsPaused)
        {
            ActivateDesktopSurfaces("state reload");
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyTheme(bool notify)
    {
        IsDarkTheme = ApplicationTheme.ResolveIsDark(State.Settings.ThemeMode);
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
        try
        {
            await SaveNowAsync();
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Autosave failed", exception);
        }
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
        _trayMenu = new FluentContextMenuStrip
        {
            MinimumMenuWidth = 210,
            ShowRootCheckMargin = true
        };
        _trayMenu.Opening += (_, _) => UpdateTrayMenu();
        _trayMenu.Opened += (_, _) => ApplyContextMenuTheme(_trayMenu);

        var showSettingsItem = new System.Windows.Forms.ToolStripMenuItem(
            "打开 CrabDesk",
            null,
            (_, _) => _beginInvoke(() => RequestShowSettings()));
        showSettingsItem.Font = new System.Drawing.Font(showSettingsItem.Font, System.Drawing.FontStyle.Bold);
        _trayMenu.Items.Add(showSettingsItem);
        _trayMenu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(
            "智能整理",
            null,
            (_, _) => _beginInvoke(() => SmartOrganize())));
        _trayMenu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(
            "新建盒子",
            null,
            (_, _) => _beginInvoke(() => AddBox())));
        _trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        _pauseTrayItem = new System.Windows.Forms.ToolStripMenuItem(
            "暂停桌面接管",
            null,
            (_, _) => _beginInvoke(() =>
            {
                SetPaused(!IsPaused);
                UpdateTrayMenu();
            }));
        _trayMenu.Items.Add(_pauseTrayItem);
        _trayMenu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(
            "重新连接桌面",
            null,
            (_, _) => _beginInvoke(async () => await ReconnectDesktopAsync())));

        _startupTrayItem = new System.Windows.Forms.ToolStripMenuItem(
            "开机启动",
            null,
            (_, _) => _beginInvoke(() =>
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
            (_, _) => _beginInvoke(RequestExit)));

        _applicationIcon = LoadApplicationIcon();
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "CrabDesk 桌面整理",
            Icon = _applicationIcon ?? System.Drawing.SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = _trayMenu
        };
        _trayIcon.DoubleClick += (_, _) => _beginInvoke(() => RequestShowSettings());
        _trayIcon.MouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == System.Windows.Forms.MouseButtons.Left)
            {
                _beginInvoke(() => RequestShowSettings());
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
        if (_trayMenu is not null) ApplyContextMenuTheme(_trayMenu);
    }

    internal void ApplyContextMenuTheme(System.Windows.Forms.ContextMenuStrip menu)
    {
        if (menu is FluentContextMenuStrip animatedMenu)
        {
            animatedMenu.AnimationsEnabled = State.Settings.Appearance.AnimationEnabled &&
                System.Windows.Forms.SystemInformation.IsMenuAnimationEnabled &&
                System.Windows.Forms.SystemInformation.IsMenuFadeEnabled;
        }
        menu.Renderer = IsDarkTheme ? _darkTrayRenderer : _lightTrayRenderer;
        menu.Font = _menuFont;
        menu.Padding = new System.Windows.Forms.Padding(5);
        var minimumWidth = menu is FluentContextMenuStrip fluentMenu
            ? fluentMenu.MinimumMenuWidth
            : 112;
        var menuWidth = Math.Max(minimumWidth, menu.GetPreferredSize(System.Drawing.Size.Empty).Width);
        menu.MinimumSize = new System.Drawing.Size(menuWidth, 0);
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = menu is not FluentContextMenuStrip rootMenu || rootMenu.ShowRootCheckMargin;
        menu.DropShadowEnabled = true;
        ApplyTrayColors(menu.Items, IsDarkTheme);
        menu.BackColor = IsDarkTheme
            ? System.Drawing.Color.FromArgb(37, 40, 45)
            : System.Drawing.Color.FromArgb(252, 252, 252);
        menu.ForeColor = IsDarkTheme
            ? System.Drawing.Color.FromArgb(244, 245, 247)
            : System.Drawing.Color.FromArgb(32, 36, 42);
        ApplyMenuMetrics(menu.Items, menuWidth - menu.Padding.Horizontal);
        menu.PerformLayout();
        if (menu.Width > 0 && menu.Height > 0)
        {
            FluentMenuRenderer.ApplyRoundedCorners(menu);
        }
    }

    private void ApplyMenuMetrics(
        System.Windows.Forms.ToolStripItemCollection items,
        int availableWidth)
    {
        foreach (System.Windows.Forms.ToolStripItem item in items)
        {
            if (item is System.Windows.Forms.ToolStripSeparator)
            {
                item.Margin = new System.Windows.Forms.Padding(8, 3, 8, 3);
                item.AutoSize = false;
                item.Width = Math.Max(1, availableWidth - item.Margin.Horizontal);
                continue;
            }
            item.Padding = new System.Windows.Forms.Padding(8, 0, 8, 0);
            item.Margin = new System.Windows.Forms.Padding(1, 0, 1, 0);
            const int itemHeight = 32;
            item.AutoSize = false;
            item.Size = new System.Drawing.Size(
                Math.Max(1, availableWidth - item.Margin.Horizontal),
                itemHeight);
            if (item is System.Windows.Forms.ToolStripMenuItem menuItem)
            {
                var dropDown = menuItem.DropDown;
                dropDown.Renderer = IsDarkTheme ? _darkTrayRenderer : _lightTrayRenderer;
                dropDown.Font = _menuFont;
                dropDown.Padding = new System.Windows.Forms.Padding(5);
                dropDown.BackColor = IsDarkTheme
                    ? System.Drawing.Color.FromArgb(37, 40, 45)
                    : System.Drawing.Color.FromArgb(252, 252, 252);
                dropDown.ForeColor = IsDarkTheme
                    ? System.Drawing.Color.FromArgb(244, 245, 247)
                    : System.Drawing.Color.FromArgb(32, 36, 42);
                if (dropDown is System.Windows.Forms.ToolStripDropDownMenu dropDownMenu)
                {
                    dropDownMenu.ShowImageMargin = false;
                    dropDownMenu.ShowCheckMargin = true;
                }
                var dropDownWidth = Math.Max(96, dropDown.GetPreferredSize(System.Drawing.Size.Empty).Width);
                dropDown.MinimumSize = new System.Drawing.Size(dropDownWidth, 0);
                ApplyMenuMetrics(
                    menuItem.DropDownItems,
                    dropDownWidth - dropDown.Padding.Horizontal);
                dropDown.PerformLayout();
                _ = _configuredSubmenus.GetValue(dropDown, candidate =>
                {
                    candidate.Opened += OnSubmenuOpened;
                    return new object();
                });
                StretchDropDownItems(dropDown);
                if (dropDown.Width > 0 && dropDown.Height > 0)
                {
                    FluentMenuRenderer.ApplyRoundedCorners(dropDown);
                }
            }
        }
    }

    private static void OnSubmenuOpened(object? sender, EventArgs eventArgs)
    {
        if (sender is not System.Windows.Forms.ToolStripDropDown dropDown)
        {
            return;
        }
        StretchDropDownItems(dropDown);
        FluentMenuRenderer.ApplyRoundedCorners(dropDown);
        dropDown.Invalidate(true);
    }

    private static void StretchDropDownItems(System.Windows.Forms.ToolStripDropDown dropDown)
    {
        if (dropDown.ClientSize.Width <= 0)
        {
            return;
        }
        foreach (System.Windows.Forms.ToolStripItem item in dropDown.Items)
        {
            var width = Math.Max(
                1,
                dropDown.ClientSize.Width -
                dropDown.Padding.Right -
                item.Bounds.Left -
                item.Margin.Right);
            item.AutoSize = false;
            item.Width = width;
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
            (_, _) => _beginInvoke(() => SetThemeMode(mode)));
        _themeTrayItems[mode] = item;
        parent.DropDownItems.Add(item);
    }

    private static void ApplyTrayColors(System.Windows.Forms.ToolStripItemCollection items, bool isDark)
    {
        var background = isDark
            ? System.Drawing.Color.FromArgb(37, 40, 45)
            : System.Drawing.Color.FromArgb(252, 252, 252);
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
            return Environment.ProcessPath is { } processPath
                ? System.Drawing.Icon.ExtractAssociatedIcon(processPath)
                : null;
        }
        catch
        {
            return null;
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
        try
        {
            await RefreshItemsAsync();
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Desktop item refresh failed", exception);
        }
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

}
