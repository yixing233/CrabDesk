using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Drawing;
using CrabDesk.Core;
using CrabDesk.Native;

namespace CrabDesk.Runtime;

public sealed class ShowSettingsRequestedEventArgs(string? page) : EventArgs
{
    public string? Page { get; } = page;
}

public sealed record DesktopConfirmationRequest(
    IntPtr OwnerHandle,
    string Title,
    string Message,
    string PrimaryText);

public sealed partial class CrabDeskRuntime : IDisposable
{
    private static readonly TimeSpan DesktopViewRefreshInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DesktopViewRefreshWindow = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DesktopMenuRefreshDelay = TimeSpan.FromMilliseconds(120);
    // A desktop submenu command is observed on mouse-down, just before
    // Explorer commits its new SortColumns value. Keep the replacement layer
    // in its sort-pending state for this short interval so a saved manual
    // layout cannot win the first redraw.
    private static readonly TimeSpan DesktopSortCommandMinimumWait = TimeSpan.FromMilliseconds(160);
    // How long a file operation CrabDesk performed itself stays recognizable in
    // the watcher notifications it causes. Those notifications arrive a quarter
    // second after each write, and a slow clipboard owner or a large selection
    // can delay the operation well past its own start.
    private static readonly TimeSpan TargetedDesktopRefreshWindow = TimeSpan.FromSeconds(10);
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
    private readonly TavilySearchService _tavilySearchService = new();
    private readonly IUpdateService _updateService = new GitHubUpdateService();
    private readonly ShellIconProvider _iconProvider = new();
    private readonly RuntimeTimer _hostTimer;
    private static readonly TimeSpan[] TakeoverRetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(120)
    ];
    private readonly RuntimeTimer _takeoverRetryTimer;
    private bool _pausedByTakeoverFailure;
    private int _takeoverRetryAttempt;
    private readonly RuntimeTimer _uiHeartbeatTimer;
    private readonly RuntimeTimer _saveTimer;
    private readonly RuntimeTimer _desktopZoomTimer;
    private readonly RuntimeTimer _desktopViewRefreshTimer;
    private readonly RuntimeTimer _desktopMenuRefreshTimer;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly SemaphoreSlim _mappedRefreshLock = new(1, 1);
    private readonly SemaphoreSlim _updateLock = new(1, 1);

    /// <summary>
    /// Serializes paste operations. A queued burst of Ctrl+V — which is what a
    /// UI-thread stall flushes once it recovers — would otherwise start several
    /// pastes that interleave at their awaits, each running a clipboard COM read
    /// and a full desktop enumeration, turning one hiccup into a cascade.
    /// Serializing rather than dropping keeps every paste the user asked for.
    /// </summary>
    private readonly SemaphoreSlim _pasteGate = new(1, 1);
    private readonly AiOrganizationOperationGate _aiOrganizationGate = new();
    private readonly CancellationTokenSource _updateCancellation = new();
    private readonly Dictionary<Guid, MappedFolderSnapshot> _mappedFolderSnapshots = [];
    private readonly Dictionary<string, FileAttributes> _originalFileAttributes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int?> _hiddenShellIconOriginals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<HotkeyAction, HotkeyRegistrationStatus> _hotkeyStatuses = [];
    private readonly HashSet<string> _targetedDesktopRefreshPaths = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _targetedDesktopRefreshExpiresAt;
    private IReadOnlyList<DesktopItemRef> _allDesktopItems = [];
    private Dictionary<string, Guid>? _lastOrganizationAssignments;
    private HashSet<Guid> _lastOrganizationCreatedBoxes = [];
    private long _workspaceRevision;
    private DesktopSurfaceManager? _surfaceManager;
    private readonly ConditionalWeakTable<System.Windows.Forms.ToolStripDropDown, object> _configuredSubmenus = new();
    private readonly FluentMenuRenderer _lightTrayRenderer = new(false);
    private readonly FluentMenuRenderer _darkTrayRenderer = new(true);
    private readonly System.Drawing.Font _menuFont = new("Segoe UI", 10, System.Drawing.FontStyle.Regular);
    private bool _disposed;
    private bool _hostCheckInProgress;
    private DateTimeOffset _lastMappedHealthCheckAt;
    private string? _verifiedUpdateInstallerPath;
    private string? _verifiedUpdateSha256;
    private string _desktopSortSignature = string.Empty;
    private string _desktopSystemIconVisibilitySignature = string.Empty;
    private DesktopIconSortState _desktopSortState;
    private bool _desktopSortStateIsAuthoritative;
    private bool _desktopAutoArrange;
    private bool _desktopIconsVisible = true;
    private bool _virtualBoxDesktopDropEnabled;
    private DateTimeOffset? _desktopViewRefreshDeadline;
    private bool _desktopMenuRefreshPending;
    private bool _desktopMenuRefreshInProgress;
    private bool _desktopSortCommandPending;
    private DateTimeOffset? _desktopSortCommandReadyAt;
    // Set only for the rebuild an explicit Refresh performs. It is kept apart
    // from _desktopSortCommandPending so neither path can consume the other's
    // one-shot redraw when both land in the same message pump turn.
    private bool _desktopRefreshResortPending;
    private readonly object _boxDragWheelGate = new();
    private int _pendingBoxDragWheelDelta;
    private Point _pendingBoxDragWheelPoint;
    private bool _boxDragWheelDispatchQueued;
    private long _lastUiHeartbeatTimestamp;
    private RuntimeTimerLatency _lastUiHeartbeatLatency;

    public CrabDeskRuntime(Action<Action> beginInvoke)
    {
        _beginInvoke = beginInvoke;
        UiThreadWatchdog.Start();
        _hostTimer = new RuntimeTimer(
            TimeSpan.FromSeconds(2),
            true,
            beginInvoke,
            () => OnHostTimer(null, EventArgs.Empty),
            "host timer");
        _uiHeartbeatTimer = new RuntimeTimer(
            TimeSpan.FromMilliseconds(250),
            true,
            beginInvoke,
            OnUiHeartbeat,
            "ui heartbeat",
            latency => _lastUiHeartbeatLatency = latency);
        _takeoverRetryTimer = new RuntimeTimer(
            TimeSpan.FromSeconds(2),
            false,
            beginInvoke,
            OnTakeoverRetryTick,
            "takeover retry");
        _saveTimer = new RuntimeTimer(
            TimeSpan.FromMilliseconds(350),
            false,
            beginInvoke,
            () => OnSaveTimer(null, EventArgs.Empty),
            "autosave");
        _desktopZoomTimer = new RuntimeTimer(
            TimeSpan.FromMilliseconds(250),
            false,
            beginInvoke,
            SynchronizeDesktopIconZoom,
            "desktop zoom sync");
        _desktopViewRefreshTimer = new RuntimeTimer(
            DesktopViewRefreshInterval,
            false,
            beginInvoke,
            SynchronizeExplorerDesktopView,
            "explorer view sync");
        _desktopMenuRefreshTimer = new RuntimeTimer(
            DesktopMenuRefreshDelay,
            false,
            beginInvoke,
            RefreshAfterDesktopMenuCommandAsync,
            "menu refresh");
        _itemProvider.ItemsChanged += (sender, args) =>
            BeginInvoke("desktop items changed", () => OnDesktopItemsChanged(sender, args));
        _mappedFolderProvider.ItemsChanged += (_, _) =>
            BeginInvoke("mapped folder refresh", async () => await RefreshMappedFoldersAsync());
        _hotkeyService.Pressed += OnGlobalHotkeyPressed;
        _aiOrganizationGate.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Dispatches <paramref name="action"/> to the UI thread with a watchdog
    /// breadcrumb, so a stall report can name the work that is still running.
    /// </summary>
    private void BeginInvoke(string name, Action action) => _beginInvoke(() =>
    {
        using var scope = UiThreadWatchdog.Enter(name);
        action();
    });

    public event EventHandler? Changed;
    public event EventHandler<ShowSettingsRequestedEventArgs>? ShowSettingsRequested;
    public event EventHandler? AiOrganizationRequested;
    public Func<DesktopConfirmationRequest, Task<bool>>? DesktopConfirmationHandler { get; set; }
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
    public bool IsAiOrganizationRunning => _aiOrganizationGate.IsRunning;
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

    /// <summary>
    /// Measures whether the desktop stall is Explorer's own third-party shell
    /// extensions or CrabDesk blocking on them. Read-only: it creates one
    /// temporary file per measured folder and deletes it again.
    /// </summary>
    public async Task<DesktopStallReport> RunDesktopStallDiagnosticsAsync(
        CancellationToken cancellationToken = default)
    {
        // Resolve handles here: the surface list belongs to the UI thread and
        // the measurement below runs on the thread pool.
        var desktopView = DesktopHostService.FindDesktopView();
        var crabDeskSurface = _surfaceManager?.GetIconSurfaceHandle(
            Monitors.FirstOrDefault(monitor => monitor.IsPrimary)?.Id ?? string.Empty) ?? IntPtr.Zero;
        if (crabDeskSurface == IntPtr.Zero && Monitors.Count > 0)
        {
            crabDeskSurface = _surfaceManager?.GetIconSurfaceHandle(Monitors[0].Id) ?? IntPtr.Zero;
        }

        var diagnostics = new DesktopStallDiagnostics(desktopView, crabDeskSurface);
        var report = await diagnostics.RunAsync(cancellationToken).ConfigureAwait(false);
        DiagnosticLog.Info(
            $"Desktop stall diagnostics verdict={report.Verdict} " +
            $"idle={report.IdleBaselineMs} control={report.OrdinaryFolderMs} " +
            $"desktop={report.DesktopFolderMs} crabdesk={report.CrabDeskSurfaceMs} " +
            $"extensions={report.Extensions.Count}");
        return report;
    }

    /// <summary>Renders the stall report as plain text for the clipboard.</summary>
    public static string FormatDesktopStallReport(DesktopStallReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var lines = new List<string>
        {
            "CrabDesk desktop stall diagnostics",
            $"Captured: {report.CapturedAt:O}",
            $"Verdict: {report.Verdict}",
            $"Desktop window found: {report.DesktopFound}",
            $"Idle baseline: {FormatMs(report.IdleBaselineMs)}",
            $"Ordinary folder: {FormatMs(report.OrdinaryFolderMs)}",
            $"Desktop folder: {FormatMs(report.DesktopFolderMs)}",
            $"CrabDesk surface (same instant): {FormatMs(report.CrabDeskSurfaceMs)}",
            string.Empty,
            "Notes:"
        };
        lines.AddRange(report.Notes.Select(note => "  " + note));

        lines.Add(string.Empty);
        if (report.Extensions.Count == 0)
        {
            lines.Add("Shell extensions: none read");
        }
        else
        {
            lines.Add("Shell extensions:");
            lines.AddRange(report.Extensions.Select(entry =>
                $"  [{entry.Kind}] {entry.Product} :: {entry.Name} :: {entry.Path}"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatMs(int milliseconds) => milliseconds == DesktopStallAnalysis.NotMeasured
        ? "not measured"
        : $"{milliseconds} ms";

    public int ClearThumbnailCache()
    {
        var cleared = _iconProvider.ClearCache();
        cleared += _surfaceManager?.ClearIconCaches() ?? 0;
        _surfaceManager?.Refresh();
        return cleared;
    }

    internal bool TrayThemeMatchesCurrentTheme()
    {
        return true;
    }

    public async Task InitializeAsync()
    {
        DiagnosticLog.Info("Runtime initialization started");
        State = await _layoutStore.LoadAsync();
        _itemProvider.ShowHiddenFiles = State.Settings.ShowHiddenFiles;
        State.Settings.AiClassification.ApiKey = AiApiKeyStore.Load(GetAiApiKeyPath());
        State.Settings.AiClassification.WebSearchApiKey = AiApiKeyStore.Load(GetAiWebSearchApiKeyPath());
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
        var initialDesktopViewState = await Task.Run(ReadInitialDesktopShellState);
        _desktopSortSignature = initialDesktopViewState.Signature;
        var initialSortState = ResolveInitialDesktopSortState(initialDesktopViewState, State);
        _desktopSortState = initialSortState ?? initialDesktopViewState.Sort;
        _desktopSortStateIsAuthoritative = initialSortState.HasValue;
        if (initialDesktopViewState.HasAuthoritativeSort &&
            TryStoreLastKnownDesktopSortState(State, initialDesktopViewState.Sort))
        {
            ScheduleSave();
        }
        else if (!initialDesktopViewState.HasAuthoritativeSort && initialSortState.HasValue)
        {
            DiagnosticLog.Info(
                $"Restored persisted desktop sort mode={initialSortState.Value.Mode} " +
                $"descending={initialSortState.Value.Descending} because Explorer sort is unavailable.");
        }
        _desktopAutoArrange = initialDesktopViewState.AutoArrange;
        _desktopIconsVisible = initialDesktopViewState.DesktopIconsVisible;
        _desktopSystemIconVisibilitySignature =
            DesktopItemProvider.GetSystemDesktopIconVisibilitySignature();
        if (State.Settings.TakeOverDesktop)
        {
            ConfigureDesktopInputMonitor();
        }
        Monitors = _monitorService.GetMonitors();
        NormalizeMonitorIds();
        LayoutCoordinator.NormalizeForMonitors(State, Monitors);
        await RefreshItemsAsync(false);
        RestoreLegacyAssignedDesktopItemVisibility();
        await RunScheduledBackupIfNeededAsync();
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

        _hostTimer.Start();
        _lastUiHeartbeatTimestamp = Stopwatch.GetTimestamp();
        _uiHeartbeatTimer.Start();
        ScheduleSave();
        if (State.Settings.Updates.CheckOnStartup)
        {
            _ = CheckForUpdatesAsync(false);
        }
        DiagnosticLog.Info($"Runtime initialization completed paused={IsPaused} monitors={Monitors.Count} items={Items.Count}");
    }

    private DesktopIconViewState ReadInitialDesktopShellState()
    {
        var viewState = DesktopIconPositionService.GetDesktopViewState();
        const int spacingAttempts = 3;
        for (var attempt = 0; attempt < spacingAttempts; attempt++)
        {
            if (DesktopIconPositionService.TryGetItemSpacing(_desktopHost.DesktopListView, out _))
            {
                break;
            }
            if (attempt + 1 < spacingAttempts)
            {
                Thread.Sleep(100);
            }
        }
        return viewState;
    }

    public IReadOnlyList<DesktopItemRef> GetItemsForBox(Guid boxId)
    {
        var box = State.Boxes.First(candidate => candidate.Id == boxId);
        IEnumerable<DesktopItemRef> query = box.IsMappedFolder
            ? _mappedFolderSnapshots.GetValueOrDefault(boxId)?.Items ?? []
            : Items.Where(item =>
                State.Assignments.TryGetValue(item.Key.ToString(), out var assignedBox) && assignedBox == boxId);
        return OrderItemsForBox(box, query);
    }

    // Resolves an item by its stable key anywhere it may live: the desktop
    // folder snapshot first, then every box (including mapped folders), so
    // drag ghosts can always show the real icon.
    internal DesktopItemRef? FindItemByKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var direct = Items.FirstOrDefault(item =>
            string.Equals(item.Key.ToString(), key, StringComparison.OrdinalIgnoreCase));
        if (direct is not null)
        {
            return direct;
        }

        foreach (var box in State.Boxes)
        {
            var candidate = GetItemsForBox(box.Id).FirstOrDefault(item =>
                string.Equals(item.Key.ToString(), key, StringComparison.OrdinalIgnoreCase));
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Produces the target box order that AssignItems would create without
    /// changing assignments, item order, or the current manual sort.
    /// </summary>
    internal IReadOnlyList<DesktopItemRef> GetItemsForBoxAfterAssigning(
        Guid boxId,
        IEnumerable<string> itemKeys)
    {
        var box = State.Boxes.First(candidate => candidate.Id == boxId);
        if (box.IsMappedFolder)
        {
            return GetItemsForBox(boxId);
        }

        return ProjectItemsForBoxAfterAssigning(box, Items, State.Assignments, itemKeys);
    }

    internal static IReadOnlyList<DesktopItemRef> ProjectItemsForBoxAfterAssigning(
        DesktopBox box,
        IReadOnlyList<DesktopItemRef> items,
        IReadOnlyDictionary<string, Guid> assignments,
        IEnumerable<string> itemKeys)
    {
        if (box.IsMappedFolder)
        {
            return OrderItemsForBox(box, []);
        }

        var itemsByKey = items.ToDictionary(
            item => item.Key.ToString(),
            StringComparer.OrdinalIgnoreCase);
        var pendingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingOrder = new List<string>();
        foreach (var requestedKey in itemKeys
                     .Where(key => !string.IsNullOrWhiteSpace(key))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!itemsByKey.TryGetValue(requestedKey, out var item))
            {
                continue;
            }

            var itemKey = item.Key.ToString();
            pendingKeys.Add(itemKey);
            pendingOrder.Add(itemKey);
        }

        if (pendingKeys.Count == 0)
        {
            return OrderItemsForBox(
                box,
                items.Where(item =>
                    assignments.TryGetValue(item.Key.ToString(), out var assignedBox) &&
                    assignedBox == box.Id));
        }

        var projectedItemOrder = box.ItemOrder
            .Where(key => !pendingKeys.Contains(key))
            .Concat(pendingOrder)
            .ToArray();
        var projectedItems = items.Where(item =>
            pendingKeys.Contains(item.Key.ToString()) ||
            (assignments.TryGetValue(item.Key.ToString(), out var assignedBox) && assignedBox == box.Id));
        return OrderItemsForBox(box, projectedItems, projectedItemOrder);
    }

    private static IReadOnlyList<DesktopItemRef> OrderItemsForBox(
        DesktopBox box,
        IEnumerable<DesktopItemRef> items,
        IReadOnlyList<string>? manualItemOrder = null)
    {
        var order = manualItemOrder ?? box.ItemOrder;
        var query = box.SortMode switch
        {
            BoxSortMode.Name => items.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            BoxSortMode.Type => items.OrderBy(item => item.Kind).ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            BoxSortMode.Modified => items.OrderByDescending(item => item.ModifiedAt).ThenBy(item => item.DisplayName),
            _ => items.OrderBy(item =>
            {
                var index = FindItemOrderIndex(order, item.Key.ToString());
                return index < 0 ? int.MaxValue : index;
            })
        };
        return query.ToArray();
    }

    private static int FindItemOrderIndex(IReadOnlyList<string> itemOrder, string itemKey)
    {
        for (var index = 0; index < itemOrder.Count; index++)
        {
            if (string.Equals(itemOrder[index], itemKey, StringComparison.Ordinal))
            {
                return index;
            }
        }
        return -1;
    }

    internal IReadOnlyList<DesktopItemRef> GetUnassignedDesktopItems() =>
        Items.Where(item => !State.Assignments.ContainsKey(item.Key.ToString())).ToArray();

    // Read while a surface rebuilds its grid, so it uses the shared short-lived
    // view snapshot instead of crossing into Explorer again for every rebuild.
    internal bool IsDesktopAutoArrangeEnabled =>
        DesktopIconPositionService.GetCachedDesktopViewState().AutoArrange;

    // True while a geometry rebuild must ignore the persisted manual grid and
    // lay every icon out in the resolved sort order: once after a native Sort
    // by command, and for the rebuild an explicit Refresh performs.
    internal bool IsDesktopResortPending =>
        _desktopSortCommandPending || _desktopRefreshResortPending;

    internal DesktopIconSortState DesktopSortState => _desktopSortState;

    internal bool TryDropDesktopItemsIntoBox(
        System.Drawing.Point screenPoint,
        IReadOnlyList<string> itemKeys) =>
        _surfaceManager?.TryDropDesktopItemsIntoBox(screenPoint, itemKeys) == true;

    internal bool UpdateDesktopItemDropPreview(
        System.Drawing.Point screenPoint,
        IReadOnlyList<string> itemKeys,
        out bool pointerOverBox)
    {
        if (_surfaceManager is null)
        {
            pointerOverBox = false;
            return false;
        }

        return _surfaceManager.UpdateDesktopItemDropPreview(
            screenPoint,
            itemKeys,
            out pointerOverBox);
    }

    internal void ClearDesktopItemDropPreviews() =>
        _surfaceManager?.ClearDesktopItemDropPreviews();

    internal void ClearDesktopBoxSelection() => _surfaceManager?.ClearBoxSelection();

    internal void ActivateDesktopKeyboardInput()
    {
        if (_disposed || IsPaused ||
            !DesktopWindowTools.TryGetDesktopInputRoot(_desktopHost.DesktopListView, out var root))
        {
            return;
        }
        // SetForegroundWindow blocks on whichever window is being deactivated. After a
        // drop from Explorer that is the still-busy source window, and the UI thread
        // sat there for seconds (watchdog scope=icon window msg=0x0201). Run it off
        // the UI thread and coalesce clicks: foreground rights are per process, so a
        // pool thread may still claim it, and keyboard focus arriving a beat late
        // beats a frozen desktop.
        if (Interlocked.Exchange(ref _desktopInputActivationPending, 1) != 0)
        {
            return;
        }
        ThreadPool.UnsafeQueueUserWorkItem(_ => ActivateDesktopInputOffThread(root), null);
    }

    private int _desktopInputActivationPending;

    private void ActivateDesktopInputOffThread(IntPtr desktopRoot)
    {
        var started = Stopwatch.StartNew();
        var activated = false;
        try
        {
            activated = DesktopWindowTools.TryActivateDesktopInput(desktopRoot);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Desktop keyboard input activation failed", exception);
        }
        finally
        {
            Volatile.Write(ref _desktopInputActivationPending, 0);
            if (started.ElapsedMilliseconds >= 100)
            {
                DiagnosticLog.Info(
                    $"Desktop input activation slow activated={activated} elapsedMs={started.ElapsedMilliseconds}");
            }
        }
    }

    // The icon surface owns the pointer-captured desktop drag. Box OLE events
    // can still arrive while that capture crosses a box; callers use this bit
    // to avoid replacing the projected slot marker with the legacy floating
    // thumbnail preview.
    internal bool IsDesktopIconPointerInteractionActive =>
        _surfaceManager?.IsDesktopIconPointerInteractionActive == true;

    internal bool IsDesktopIconDragActive =>
        _surfaceManager?.IsDesktopIconDragActive == true;

    internal bool IsVirtualBoxDesktopDropEnabled => _virtualBoxDesktopDropEnabled;

    internal void SetVirtualBoxDesktopDropEnabled(bool enabled)
    {
        _virtualBoxDesktopDropEnabled = enabled;
        _surfaceManager?.SetVirtualBoxDropTargetEnabled(enabled);
    }

    internal void ResetDesktopIconLayoutForAutoArrange(bool refreshWorkspace = true)
    {
        if (State.DesktopIconLayout.Count == 0 && State.DesktopIconPositions.Count == 0)
        {
            return;
        }

        State.DesktopIconLayout.Clear();
        State.DesktopIconPositions.Clear();
        if (refreshWorkspace)
        {
            NotifyWorkspaceChanged(true);
        }
        else
        {
            Changed?.Invoke(this, EventArgs.Empty);
            ScheduleSave();
        }
    }

    internal bool SetDesktopIconLayout(
        IEnumerable<KeyValuePair<string, DesktopIconLayoutSnapshot>> placements,
        bool refreshWorkspace = true)
    {
        var next = new Dictionary<string, DesktopIconLayoutSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var (itemKey, source) in placements)
        {
            if (string.IsNullOrWhiteSpace(itemKey) || source is null)
            {
                continue;
            }

            next[itemKey] = new DesktopIconLayoutSnapshot
            {
                MonitorId = source.MonitorId?.Trim() ?? string.Empty,
                Column = Math.Max(0, source.Column),
                Row = Math.Max(0, source.Row)
            };
        }

        if (DesktopIconLayoutsEqual(State.DesktopIconLayout, next))
        {
            return false;
        }

        State.DesktopIconLayout = next;
        // Older builds stored only moved keys. Once a full snapshot exists it
        // replaces that partial state, avoiding a later refresh that mixes
        // sorted and manually moved icons.
        State.DesktopIconPositions.Clear();
        if (refreshWorkspace)
        {
            NotifyWorkspaceChanged(true);
        }
        else
        {
            Changed?.Invoke(this, EventArgs.Empty);
            ScheduleSave();
        }
        return true;
    }

    internal bool SetDesktopIconPositions(
        IEnumerable<KeyValuePair<string, DesktopIconPlacement>> positions)
    {
        var changed = false;
        foreach (var (itemKey, source) in positions)
        {
            if (string.IsNullOrWhiteSpace(itemKey) || source is null)
            {
                continue;
            }

            var placement = new DesktopIconPlacement
            {
                MonitorId = source.MonitorId?.Trim() ?? string.Empty,
                Column = Math.Max(0, source.Column),
                Row = Math.Max(0, source.Row)
            };
            if (State.DesktopIconPositions.TryGetValue(itemKey, out var existing) &&
                string.Equals(existing.MonitorId, placement.MonitorId, StringComparison.OrdinalIgnoreCase) &&
                existing.Column == placement.Column &&
                existing.Row == placement.Row)
            {
                continue;
            }

            State.DesktopIconPositions[itemKey] = placement;
            changed = true;
        }

        if (changed)
        {
            NotifyWorkspaceChanged(true);
        }
        return changed;
    }

    private static bool DesktopIconLayoutsEqual(
        IReadOnlyDictionary<string, DesktopIconLayoutSnapshot> left,
        IReadOnlyDictionary<string, DesktopIconLayoutSnapshot> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (key, leftPlacement) in left)
        {
            if (!right.TryGetValue(key, out var rightPlacement) ||
                !string.Equals(leftPlacement.MonitorId, rightPlacement.MonitorId, StringComparison.OrdinalIgnoreCase) ||
                leftPlacement.Column != rightPlacement.Column ||
                leftPlacement.Row != rightPlacement.Row)
            {
                return false;
            }
        }

        return true;
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
        NotifyBoxItemsChanged(boxId);
        return true;
    }

    public bool MoveBoxInStack(Guid boxId, BoxStackMove move)
    {
        if (!BoxStacking.Move(State.Boxes, boxId, move))
        {
            return false;
        }

        NotifyWorkspaceChanged(true);
        return true;
    }

    public bool BringBoxToFront(Guid boxId) =>
        MoveBoxInStack(boxId, BoxStackMove.ToFront);

    public MappedFolderSnapshot? GetMappedFolderSnapshot(Guid boxId) =>
        _mappedFolderSnapshots.GetValueOrDefault(boxId);

    public DesktopBox AddBox(string title = "新盒子") => CommitNewBox(CreateBoxCore(title));

    // Creates a box where the desktop was right-clicked: its top-left corner
    // lands on the anchor (work-area DIPs of that monitor), pulled inside the
    // work area when the click was near an edge. The tray, hotkey and
    // settings paths keep the free-slot search in AddBox.
    public DesktopBox AddBoxAt(string monitorId, double x, double y, string title = "新盒子")
    {
        var monitor = Monitors.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, monitorId, StringComparison.OrdinalIgnoreCase)) ??
            Monitors.FirstOrDefault(candidate => candidate.IsPrimary) ?? Monitors.First();
        return CommitNewBox(CreateBoxCore(
            title,
            monitor,
            BoxLayoutPlanner.PlaceAt(monitor.WorkArea, x, y, 420, 310)));
    }

    private DesktopBox CommitNewBox(DesktopBox box)
    {
        if (IsPaused)
        {
            SetPaused(false);
        }
        else
        {
            NotifyBoxAdded(box.Id);
        }
        return box;
    }

    private DesktopBox CreateBoxCore(string title)
    {
        var monitor = Monitors.FirstOrDefault(candidate => candidate.IsPrimary) ?? Monitors.First();
        return CreateBoxCore(title, monitor, FindAvailableBoxBounds(monitor, 420, 310));
    }

    private DesktopBox CreateBoxCore(string title, MonitorLayout monitor, LayoutRect bounds)
    {
        var shared = State.Boxes.FirstOrDefault();
        var box = new DesktopBox
        {
            Title = title,
            MonitorId = monitor.Id,
            StackOrder = BoxStacking.GetFrontStackOrder(State.Boxes, monitor.Id),
            Bounds = bounds,
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
            StackOrder = BoxStacking.GetFrontStackOrder(State.Boxes, monitor.Id),
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
        NotifyBoxAdded(box.Id);
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
        NotifyBoxWorkspaceChanged(box.Id);
    }

    public void SetMappedFolderReadOnly(DesktopBox box, bool isReadOnly)
    {
        if (box.MappedFolder is null)
        {
            return;
        }
        box.MappedFolder.IsReadOnly = isReadOnly;
        NotifyBoxWorkspaceChanged(box.Id);
    }

    public void SetMappedFolderCategoryTabsEnabled(DesktopBox box, bool enabled)
    {
        if (box.MappedFolder is null)
        {
            return;
        }
        box.MappedFolder.EnableCategoryTabs = enabled;
        NotifyBoxWorkspaceChanged(box.Id);
    }

    public DesktopBoxTab CreateManualTab(Guid boxId, string title)
    {
        var box = State.Boxes.First(candidate => candidate.Id == boxId);
        if (box.IsMappedFolder)
        {
            throw new InvalidOperationException("映射文件夹使用自动分类标签，不能创建手动标签。");
        }

        var tab = new DesktopBoxTab
        {
            Title = GetUniqueManualTabTitle(box, title)
        };
        box.ManualTabs.Add(tab);
        // The first manual tab adds a fixed tab strip to the content stack.
        // Re-run layout normalization so legacy no-tab magnetic heights are
        // promoted before the surface is redrawn.
        LayoutCoordinator.NormalizeForMonitors(State, Monitors);
        NotifyBoxWorkspaceChanged(boxId);
        return tab;
    }

    public bool RenameManualTab(Guid boxId, Guid tabId, string title)
    {
        var box = State.Boxes.First(candidate => candidate.Id == boxId);
        var tab = box.IsMappedFolder
            ? null
            : box.ManualTabs.FirstOrDefault(candidate => candidate.Id == tabId);
        if (tab is null)
        {
            return false;
        }

        var normalizedTitle = GetUniqueManualTabTitle(box, title, tabId);
        if (string.Equals(tab.Title, normalizedTitle, StringComparison.CurrentCulture))
        {
            return false;
        }

        tab.Title = normalizedTitle;
        NotifyBoxWorkspaceChanged(boxId);
        return true;
    }

    public bool DeleteManualTab(Guid boxId, Guid tabId)
    {
        var box = State.Boxes.First(candidate => candidate.Id == boxId);
        if (box.IsMappedFolder)
        {
            return false;
        }

        var removed = box.ManualTabs.RemoveAll(tab => tab.Id == tabId);
        if (removed == 0)
        {
            return false;
        }

        foreach (var itemKey in box.ItemTabAssignments
                     .Where(pair => pair.Value == tabId)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            box.ItemTabAssignments.Remove(itemKey);
        }
        NotifyBoxWorkspaceChanged(boxId);
        return true;
    }

    public int MoveItemsToManualTab(Guid boxId, IEnumerable<string> itemKeys, Guid? tabId)
    {
        var box = State.Boxes.First(candidate => candidate.Id == boxId);
        if (box.IsMappedFolder ||
            (tabId is { } id && !box.ManualTabs.Any(tab => tab.Id == id)))
        {
            return 0;
        }

        var changed = 0;
        foreach (var itemKey in itemKeys
                     .Where(key => !string.IsNullOrWhiteSpace(key))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(key => State.Assignments.TryGetValue(key, out var assignedBoxId) && assignedBoxId == boxId))
        {
            if (tabId is { } targetTabId)
            {
                if (box.ItemTabAssignments.TryGetValue(itemKey, out var currentTabId) && currentTabId == targetTabId)
                {
                    continue;
                }
                box.ItemTabAssignments[itemKey] = targetTabId;
            }
            else if (!box.ItemTabAssignments.Remove(itemKey))
            {
                continue;
            }
            changed++;
        }

        if (changed > 0)
        {
            NotifyBoxItemsChanged(boxId);
        }
        return changed;
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
        AssignItems([itemKey], boxId);
    }

    /// <summary>
    /// Assigns a desktop selection to a normal box in one state transition.
    /// A multi-icon desktop drop produces one Explorer notification and one
    /// surface refresh, rather than repeating both once per icon.
    /// </summary>
    public int AssignItems(IEnumerable<string> itemKeys, Guid boxId)
        => AssignItemsCore(itemKeys, boxId, notify: true);

    private int AssignItemsCore(
        IEnumerable<string> itemKeys,
        Guid boxId,
        bool notify,
        Guid? targetTabId = null)
    {
        if (State.Boxes.FirstOrDefault(box => box.Id == boxId)?.IsMappedFolder != false)
        {
            return 0;
        }

        var itemsByKey = Items.ToDictionary(
            item => item.Key.ToString(),
            StringComparer.OrdinalIgnoreCase);
        var assignedKeys = new List<string>();
        foreach (var requestedKey in itemKeys
                     .Where(key => !string.IsNullOrWhiteSpace(key))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!itemsByKey.TryGetValue(requestedKey, out var item))
            {
                continue;
            }

            var itemKey = item.Key.ToString();
            State.Assignments[itemKey] = boxId;
            MoveItemOrderKey(itemKey, boxId, targetTabId: targetTabId);
            assignedKeys.Add(itemKey);
        }
        if (assignedKeys.Count == 0)
        {
            return 0;
        }

        if (notify)
        {
            NotifyDesktopItemsAssignedToBox(boxId, assignedKeys);
        }
        return assignedKeys.Count;
    }

    internal int AssignDesktopItemsAtDrop(
        IEnumerable<string> itemKeys,
        Guid boxId,
        string? beforeKey = null,
        Guid? targetTabId = null)
    {
        if (State.Boxes.FirstOrDefault(box => box.Id == boxId)?.IsMappedFolder != false)
        {
            return 0;
        }

        var itemsByKey = Items.ToDictionary(
            item => item.Key.ToString(),
            StringComparer.OrdinalIgnoreCase);
        var assignedKeys = new List<string>();
        foreach (var requestedKey in itemKeys
                     .Where(key => !string.IsNullOrWhiteSpace(key))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!itemsByKey.TryGetValue(requestedKey, out var item))
            {
                continue;
            }

            var itemKey = item.Key.ToString();
            State.Assignments[itemKey] = boxId;
            MoveItemOrderKey(itemKey, boxId, beforeKey, targetTabId);
            assignedKeys.Add(itemKey);
        }
        if (assignedKeys.Count == 0)
        {
            return 0;
        }

        NotifyDesktopItemAssignmentChanged(boxId);
        return assignedKeys.Count;
    }

    private void NotifyDesktopItemAssignmentChanged(Guid boxId)
    {
        NotifyBoxItemsChanged(boxId);
    }

    // Assigning desktop items to a box changes that box and the desktop cells
    // the items vacated. Refresh exactly those instead of rebuilding every
    // surface, which repaints all monitors and reloads their icon caches.
    private void NotifyDesktopItemsAssignedToBox(
        Guid boxId,
        IReadOnlyCollection<string> itemKeys) =>
        NotifyTargetedWorkspaceChangedOrRefresh(manager =>
            manager.RefreshDesktopItemsAssigned(boxId, itemKeys));

    private void NotifyBoxItemsChanged(Guid boxId) =>
        NotifyTargetedWorkspaceChanged(manager => manager.RefreshBoxItems(boxId));

    private void NotifyBoxesItemsChanged(IReadOnlyCollection<Guid> boxIds) =>
        NotifyTargetedWorkspaceChanged(manager => manager.RefreshBoxItems(boxIds));

    private void NotifyBoxAdded(Guid boxId) =>
        NotifyTargetedWorkspaceChanged(manager => manager.RefreshBoxAdded(boxId));

    // A targeted refresh that reports it repainted nothing has left the
    // surfaces stale, so it falls back to the full pass. The fallback keeps the
    // narrow paths honest: they can be precise without risking a missing icon.
    private void NotifyTargetedWorkspaceChangedOrRefresh(Func<DesktopSurfaceManager, bool> refresh)
    {
        var diagnosticStarted = Stopwatch.StartNew();
        var refreshed = false;
        _workspaceRevision++;
        try
        {
            if (_surfaceManager is not null)
            {
                refreshed = refresh(_surfaceManager);
                if (!refreshed)
                {
                    _surfaceManager.Refresh();
                }
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Targeted desktop surface refresh failed after a workspace change", exception);
        }
        var refreshElapsed = diagnosticStarted.ElapsedMilliseconds;
        var changedStarted = Stopwatch.StartNew();
        Changed?.Invoke(this, EventArgs.Empty);
        var changedElapsed = changedStarted.ElapsedMilliseconds;
        ScheduleSave();
        DiagnosticLog.Info(
            "Paste-path timing " +
            $"refreshMs={refreshElapsed} changedMs={changedElapsed} totalMs={diagnosticStarted.ElapsedMilliseconds} " +
            $"targeted={refreshed}");
    }

    private void NotifyTargetedWorkspaceChanged(Action<DesktopSurfaceManager> refresh)
    {
        _workspaceRevision++;
        try
        {
            if (_surfaceManager is not null)
            {
                refresh(_surfaceManager);
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Targeted desktop surface refresh failed after a workspace change", exception);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
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

    private void NotifyBoxWorkspaceChanged(Guid? boxId)
    {
        if (boxId is { } id)
        {
            NotifyTargetedWorkspaceChanged(manager => manager.RefreshBox(id));
            return;
        }

        NotifyWorkspaceChanged(true);
    }

    // A paste adds a known set of files, so the desktop only repaints the cells
    // those items occupy. An imported path that cannot be matched to a desktop
    // item leaves the affected cells unknown, so that case still refreshes all.
    private void NotifyDesktopItemsAdded(IReadOnlyCollection<string> importedPaths)
    {
        var addedKeys = ResolveDesktopItemKeys(importedPaths);
        if (addedKeys.Count != importedPaths.Count)
        {
            NotifyWorkspaceChanged(true);
            return;
        }

        // A stored assignment can already claim an imported file (a stable file
        // key is reused after its previous owner is gone), and such an item
        // joins that box instead of taking a desktop cell.
        var assignedBoxIds = addedKeys
            .Select(key => State.Assignments.TryGetValue(key, out var boxId) ? (Guid?)boxId : null)
            .OfType<Guid>()
            .ToHashSet();
        var desktopKeys = addedKeys
            .Where(key => !State.Assignments.ContainsKey(key))
            .ToArray();
        NotifyTargetedWorkspaceChangedOrRefresh(manager =>
        {
            var refreshedDesktop = desktopKeys.Length == 0 ||
                manager.RefreshDesktopItemsAdded(desktopKeys);
            var refreshedBoxes = assignedBoxIds.Count == 0 ||
                manager.RefreshBoxItems(assignedBoxIds);
            return refreshedDesktop && refreshedBoxes;
        });
    }

    // A deletion only vacates the cells its items occupied and the boxes that
    // held them, so the surfaces repaint exactly those instead of rebuilding
    // every monitor. Registering the paths lets the watcher notification that
    // follows be reconciled against the new snapshot rather than triggering a
    // second full pass a quarter second later.
    internal async Task RefreshAfterDesktopItemsDeletedAsync(IEnumerable<string> deletedPaths)
    {
        var paths = deletedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        RegisterTargetedDesktopRefresh(paths);
        var removedKeys = ResolveDesktopItemKeys(paths);
        var changedBoxIds = removedKeys
            .Select(key => State.Assignments.TryGetValue(key, out var boxId) ? (Guid?)boxId : null)
            .OfType<Guid>()
            .ToHashSet();
        // A mapped box mirrors a real folder instead of the desktop, so its
        // deleted items are absent from the desktop snapshot and are matched by
        // path against the snapshot taken before the delete.
        foreach (var box in State.Boxes.Where(box => box.IsMappedFolder))
        {
            if (GetMappedFolderSnapshot(box.Id)?.Items.Any(item =>
                    item.FileSystemPath is not null &&
                    paths.Contains(Path.GetFullPath(item.FileSystemPath))) == true)
            {
                changedBoxIds.Add(box.Id);
            }
        }
        var removedDesktopKeys = removedKeys
            .Where(key => !State.Assignments.ContainsKey(key))
            .ToArray();
        await RefreshItemsCoreAsync(refreshSurfaces: false, applyDesktopRules: false);
        // A path that no surface was rendering (an external file dragged onto
        // the Recycle Bin) leaves every surface exactly as it was.
        if (removedKeys.Count == 0 && changedBoxIds.Count == 0)
        {
            DiagnosticLog.Info($"Desktop deletion left every surface unchanged paths={paths.Count}");
            return;
        }

        var refreshedDesktop = removedDesktopKeys.Length == 0 ||
            _surfaceManager?.RefreshDesktopItemsRemoved(removedDesktopKeys) == true;
        var refreshedBoxes = changedBoxIds.Count == 0 ||
            _surfaceManager?.RefreshBoxItems(changedBoxIds) == true;
        if (refreshedDesktop && refreshedBoxes)
        {
            DiagnosticLog.Info(
                "Desktop deletion refreshed without a full pass " +
                $"removed={removedDesktopKeys.Length} boxes={changedBoxIds.Count}");
            return;
        }

        _surfaceManager?.Refresh();
    }

    private IReadOnlyCollection<string> ResolveDesktopItemKeys(IEnumerable<string> paths)
    {
        var itemsByPath = Items
            .Where(item => item.FileSystemPath is not null)
            .GroupBy(item => Path.GetFullPath(item.FileSystemPath!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (itemsByPath.TryGetValue(Path.GetFullPath(path), out var item))
            {
                keys.Add(item.Key.ToString());
            }
        }
        return keys;
    }

    /// <summary>
    /// Releases virtual box items back to Explorer. Explorer owns both the
    /// timing and placement of its icons; CrabDesk never polls or writes the
    /// desktop ListView during a drag.
    /// </summary>
    public Task<bool> ReleaseAssignedItemsToDesktopAsync(
        IEnumerable<string> itemKeys,
        System.Drawing.Point screenPoint,
        CancellationToken cancellationToken = default)
    {
        var keys = itemKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(key => State.Assignments.ContainsKey(key))
            .ToArray();
        if (keys.Length == 0)
        {
            return Task.FromResult(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var sourceBoxIds = keys
            .Select(key => State.Assignments.TryGetValue(key, out var boxId) ? (Guid?)boxId : null)
            .Where(boxId => boxId is not null)
            .Select(boxId => boxId!.Value)
            .Distinct()
            .ToArray();
        var unassignedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            if (State.Assignments.ContainsKey(key))
            {
                UnassignItemCore(key);
                unassignedKeys.Add(key);
            }
        }
        if (unassignedKeys.Count > 0)
        {
            NotifyDesktopItemReleaseChanged(unassignedKeys, sourceBoxIds);
        }
        return Task.FromResult(true);
    }

    internal static bool PathsOverlapForTargetedDesktopRefresh(string pathA, string pathB)
    {
        if (string.IsNullOrWhiteSpace(pathA) || string.IsNullOrWhiteSpace(pathB))
        {
            return false;
        }

        var fullA = Path.GetFullPath(pathA).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullB = Path.GetFullPath(pathB).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(fullA, fullB, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fullA.StartsWith(fullB + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               fullB.StartsWith(fullA + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal void RegisterTargetedDesktopRefresh(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                _targetedDesktopRefreshPaths.Add(Path.GetFullPath(path));
            }
        }
        _targetedDesktopRefreshExpiresAt = DateTimeOffset.Now + TargetedDesktopRefreshWindow;
    }

    /// <summary>
    /// Reports whether a watcher notification describes a change CrabDesk is
    /// making itself, so it can be reconciled against the new snapshot instead
    /// of rebuilding every surface. A match extends the window: copying a large
    /// selection produces a cascade of notifications, and the operation is
    /// still the owner of the last one even when it outran the initial window.
    /// </summary>
    internal bool ShouldSuppressTargetedDesktopRefresh(string path)
    {
        if (DateTimeOffset.Now > _targetedDesktopRefreshExpiresAt)
        {
            _targetedDesktopRefreshPaths.Clear();
            return false;
        }
        if (!_targetedDesktopRefreshPaths.Any(target =>
                PathsOverlapForTargetedDesktopRefresh(target, path)))
        {
            return false;
        }

        _targetedDesktopRefreshExpiresAt = DateTimeOffset.Now + TargetedDesktopRefreshWindow;
        return true;
    }

    internal async Task RefreshItemsSnapshotAsync(bool applyDesktopRules = false)
    {
        await RefreshItemsCoreAsync(refreshSurfaces: false, applyDesktopRules: applyDesktopRules);
    }

    internal void RefreshDesktopSurfaces()
    {
        _surfaceManager?.Refresh();
    }

    internal void RefreshDesktopItemsChanged(IReadOnlyCollection<string> changedItemKeys)
    {
        _surfaceManager?.RefreshDesktopItemsAdded(changedItemKeys);
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    internal bool CommitActiveDesktopInlineRename() =>
        _surfaceManager?.CommitActiveInlineRename() == true;

    internal void PrepareDesktopSelection(
        DesktopIconSurface source,
        bool preserveExisting) =>
        _surfaceManager?.PrepareSelection(source, preserveExisting);

    internal void PrepareDesktopSelection(
        DesktopBoxForm source,
        bool preserveExisting) =>
        _surfaceManager?.PrepareSelection(source, preserveExisting);

    internal void ClearDesktopSelection() =>
        _surfaceManager?.ClearSelection();

    internal void CompleteDesktopPointerInteraction()
    {
        _surfaceManager?.CompleteDesktopPointerInteraction();
    }

    internal async Task<bool> ReleaseAssignedItemsToDesktopAtDropAsync(
        IEnumerable<string> itemKeys,
        IReadOnlyDictionary<string, DesktopIconLayoutSnapshot> layout)
    {
        if (!ReleaseAssignedItemsToDesktopCore(itemKeys, layout))
        {
            return false;
        }

        return await Task.FromResult(true);
    }

    private bool ReleaseAssignedItemsToDesktopCore(
        IEnumerable<string> itemKeys,
        IReadOnlyDictionary<string, DesktopIconLayoutSnapshot> layout)
    {
        var unassignedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceBoxIds = new HashSet<Guid>();
        foreach (var key in itemKeys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (State.Assignments.TryGetValue(key, out var sourceBoxId))
            {
                sourceBoxIds.Add(sourceBoxId);
            }
            if (State.Assignments.Remove(key))
            {
                unassignedKeys.Add(key);
            }
        }

        if (unassignedKeys.Count == 0 && layout.Count == 0)
        {
            return false;
        }

        SetDesktopIconLayout(layout, refreshWorkspace: false);
        NotifyDesktopItemReleaseChanged(unassignedKeys, sourceBoxIds);
        return true;
    }

    private void NotifyDesktopItemReleaseChanged(IReadOnlySet<string> releasedItemKeys, IReadOnlyCollection<Guid>? sourceBoxIds = null)
    {
        var boxIds = sourceBoxIds ?? State.Boxes.Select(box => box.Id).ToArray();
        _surfaceManager?.RefreshDesktopItemRelease(boxIds, releasedItemKeys.ToArray());
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    // The visual desktop surface owns only presentation. It never hides a
    // desktop file through attributes: hidden/system files disappear from
    // common file dialogs as well as the desktop. Repair the legacy marker
    // once at startup, then leave file visibility entirely to Windows.
    private void RestoreLegacyAssignedDesktopItemVisibility()
    {
        RestoreAssignedItemVisibility(true);
        var restoredFiles = 0;
        var restoredShells = 0;
        foreach (var item in GetAssignedDesktopItems())
        {
            if (item.FileSystemPath is { } pathValue && !string.IsNullOrWhiteSpace(pathValue))
            {
                try
                {
                    var path = Path.GetFullPath(pathValue);
                    var attributes = File.GetAttributes(path);
                    var legacyMarker = FileAttributes.Hidden | FileAttributes.System;
                    if ((attributes & legacyMarker) == legacyMarker)
                    {
                        File.SetAttributes(path, attributes & ~legacyMarker);
                        restoredFiles++;
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
            if (item.Kind != DesktopItemKind.Shell ||
                item.ParsingName is not { } parsingName ||
                !TryGetShellClsid(parsingName, out var clsid) ||
                !WriteShellIconVisibility(clsid, null))
            {
                continue;
            }
            restoredShells++;
        }
        DiagnosticLog.Info($"Legacy assigned item visibility restored files={restoredFiles} shells={restoredShells}");
    }

    private static bool TryGetShellClsid(string parsingName, out string clsid)
    {
        clsid = string.Empty;
        const string prefix = "shell:::";
        if (!parsingName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        clsid = parsingName[prefix.Length..].Trim();
        return Guid.TryParse(clsid, out _);
    }

    private const string HideDesktopIconsNewStartPanelKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";
    private const string HideDesktopIconsClassicStartMenuKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\ClassicStartMenu";

    private bool HideShellDesktopIcon(string clsid)
    {
        try
        {
            if (!_hiddenShellIconOriginals.ContainsKey(clsid))
            {
                _hiddenShellIconOriginals[clsid] = ReadShellIconVisibility(clsid);
            }
            return WriteShellIconVisibility(clsid, 1);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error($"Failed to hide shell desktop icon clsid={clsid}", exception);
            _hiddenShellIconOriginals.Remove(clsid);
            return false;
        }
    }

    private static int? ReadShellIconVisibility(string clsid)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(HideDesktopIconsNewStartPanelKey);
        return key?.GetValue(clsid) is int value ? value : null;
    }

    private bool WriteShellIconVisibility(string clsid, int? value)
    {
        var changed = false;
        foreach (var subKey in new[] { HideDesktopIconsNewStartPanelKey, HideDesktopIconsClassicStartMenuKey })
        {
            if (value is { } hidden)
            {
                using var readKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subKey);
                if (readKey?.GetValue(clsid) is int existing && existing == hidden)
                {
                    continue;
                }
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(subKey);
                key.SetValue(clsid, hidden, Microsoft.Win32.RegistryValueKind.DWord);
                changed = true;
            }
            else
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subKey, writable: true);
                if (key?.GetValue(clsid) is null)
                {
                    continue;
                }
                key.DeleteValue(clsid, false);
                changed = true;
            }
        }
        return changed;
    }
    private bool RestoreOriginalFileAttributes(bool clear)
    {
        var completedPaths = new List<string>();
        var failed = false;
        foreach (var pair in _originalFileAttributes)
        {
            try
            {
                if ((File.Exists(pair.Key) || Directory.Exists(pair.Key)) &&
                    File.GetAttributes(pair.Key) != pair.Value)
                {
                    File.SetAttributes(pair.Key, pair.Value);
                }
                completedPaths.Add(pair.Key);
            }
            catch (IOException)
            {
                failed = true;
            }
            catch (UnauthorizedAccessException)
            {
                failed = true;
            }
        }
        if (clear)
        {
            foreach (var path in completedPaths)
            {
                _originalFileAttributes.Remove(path);
            }
        }
        return !failed && _originalFileAttributes.Count == 0;
    }

    private bool RestoreHiddenShellIcons(bool clear)
    {
        var failed = false;
        foreach (var pair in _hiddenShellIconOriginals)
        {
            try
            {
                WriteShellIconVisibility(pair.Key, pair.Value);
            }
            catch (Exception)
            {
                failed = true;
            }
        }
        if (clear)
        {
            _hiddenShellIconOriginals.Clear();
        }
        return !failed && _hiddenShellIconOriginals.Count == 0;
    }

    private bool RestoreAssignedItemVisibility(bool clear)
    {
        var filesComplete = RestoreOriginalFileAttributes(clear);
        var shellsComplete = RestoreHiddenShellIcons(clear);
        var complete = filesComplete && shellsComplete;
        return complete;
    }

    public void BoxChanged(
        DesktopBox box,
        bool rebuild = false,
        string? previousMonitorId = null)
    {
        var monitor = Monitors.FirstOrDefault(candidate => candidate.Id == box.MonitorId)
            ?? Monitors.FirstOrDefault(candidate => candidate.IsPrimary)
            ?? Monitors.First();
        var minimumWidth = DesktopItemLayoutEngine.GetMinimumBoxWidth(
            box.ViewMode,
            box.Appearance.IconSize,
            DesktopItemLayoutEngine.ScaleIconSpacing(State.Settings.Appearance.IconHorizontalSpacing, box.Appearance.IconSize));
        var minimumHeight = DesktopItemLayoutEngine.GetMinimumBoxHeight(
            box.ViewMode,
            box.Appearance.TitleBarHeight,
            box.Appearance.IconSize,
            DesktopItemLayoutEngine.ScaleIconSpacing(State.Settings.Appearance.IconVerticalSpacing, box.Appearance.IconSize),
            box.ManualTabs.Count > 0 ? DesktopItemLayoutEngine.TabBarHeight : 0);
        box.Bounds = box.Bounds.Clamp(
            new LayoutRect(0, 0, monitor.WorkArea.Width, monitor.WorkArea.Height),
            minimumWidth,
            minimumHeight);
        if (rebuild && !string.IsNullOrWhiteSpace(previousMonitorId))
        {
            NotifyBoxMonitorChanged(box, previousMonitorId);
            return;
        }

        NotifyWorkspaceChanged(rebuild);
    }

    private void NotifyBoxMonitorChanged(DesktopBox box, string previousMonitorId)
    {
        _workspaceRevision++;
        try
        {
            _surfaceManager?.RefreshBoxMonitorChanged(box.Id, previousMonitorId, box.MonitorId);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Targeted desktop surface refresh failed after a box monitor change", exception);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public async Task<FileImportBatchResult> ImportFilesAsync(
        IEnumerable<string> paths,
        Guid boxId,
        bool move,
        Guid? targetTabId = null)
    {
        var diagnosticStarted = Stopwatch.StartNew();
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var sourcePaths = paths.ToArray();
        RegisterTargetedDesktopRefresh(sourcePaths);
        var imported = await _fileOperations.ImportAsync(sourcePaths, desktop, move);
        var importElapsed = diagnosticStarted.ElapsedMilliseconds;
        if (imported.SucceededCount == 0)
        {
            DiagnosticLog.Info($"Box paste stages importMs={importElapsed} succeeded=0");
            return imported;
        }

        RegisterTargetedDesktopRefresh(imported.ImportedPaths);
        await RefreshItemsCoreAsync(refreshSurfaces: false);
        var refreshElapsed = diagnosticStarted.ElapsedMilliseconds - importElapsed;
        var importedSet = imported.ImportedPaths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assignedKeys = new List<string>();
        foreach (var item in Items.Where(item => item.FileSystemPath is not null && importedSet.Contains(Path.GetFullPath(item.FileSystemPath))))
        {
            State.Assignments[item.Key.ToString()] = boxId;
            MoveItemOrderKey(item.Key.ToString(), boxId, targetTabId: targetTabId);
            assignedKeys.Add(item.Key.ToString());
        }
        // Every imported file was assigned, so only the box changed. An
        // unmatched import would still be a plain desktop icon and needs the
        // full surface pass to appear.
        if (assignedKeys.Count == imported.SucceededCount)
        {
            NotifyBoxItemsChanged(boxId);
        }
        else
        {
            NotifyWorkspaceChanged(true);
        }
        DiagnosticLog.Info(
            $"Box paste stages importMs={importElapsed} refreshMs={refreshElapsed} " +
            $"assignMs={diagnosticStarted.ElapsedMilliseconds - importElapsed - refreshElapsed} " +
            $"totalMs={diagnosticStarted.ElapsedMilliseconds}");
        return imported;
    }

    public async Task<FileImportBatchResult> ImportFilesToBoxAsync(IEnumerable<string> paths, Guid boxId, bool move)
    {
        var box = State.Boxes.First(candidate => candidate.Id == boxId);
        if (!box.IsMappedFolder)
        {
            return await ImportFilesAsync(paths, boxId, move);
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
        var imported = await _fileOperations.ImportAsync(paths, box.MappedFolder.Path, move);
        if (imported.SucceededCount > 0)
        {
            await RefreshMappedFoldersAsync(false);
            NotifyBoxWorkspaceChanged(boxId);
        }
        return imported;
    }

    /// <summary>
    /// Imports files/folders into a subfolder shown inside a mapped box. The
    /// subfolder name comes from a folder item enumerated from the box root, so
    /// it is relative to that root and cannot escape it.
    /// </summary>
    public async Task<FileImportBatchResult> ImportFilesToMappedFolderSubfolderAsync(
        IEnumerable<string> paths,
        Guid boxId,
        string subfolderName,
        bool move)
    {
        var box = State.Boxes.First(candidate => candidate.Id == boxId);
        if (!box.IsMappedFolder || box.MappedFolder!.IsReadOnly)
        {
            throw new InvalidOperationException("此映射盒子已设为只读。");
        }
        var snapshot = GetMappedFolderSnapshot(boxId);
        if (snapshot?.IsAvailable != true)
        {
            throw new DirectoryNotFoundException(snapshot?.Message ?? "映射文件夹不可用。");
        }
        var targetDirectory = Path.Combine(box.MappedFolder.Path, subfolderName);
        var imported = await _fileOperations.ImportAsync(paths, targetDirectory, move);
        if (imported.SucceededCount > 0)
        {
            await RefreshMappedFoldersAsync(false);
            NotifyBoxWorkspaceChanged(boxId);
        }
        return imported;
    }

    /// <summary>
    /// Copies/moves files and folders into an arbitrary folder path (a folder
    /// item shown inside any box, or a mapped box's subfolder). Refreshes both
    /// the desktop item list and the mapped-folder snapshots afterwards so the
    /// new items appear immediately.
    /// </summary>
    public async Task<FileImportBatchResult> ImportFilesIntoFolderAsync(
        IEnumerable<string> paths,
        string targetFolderPath,
        bool move)
    {
        var imported = await _fileOperations.ImportAsync(paths, targetFolderPath, move);
        if (imported.SucceededCount > 0)
        {
            await RefreshItemsCoreAsync(refreshSurfaces: false);
            await RefreshMappedFoldersAsync(false);
            NotifyWorkspaceChanged(true);
        }
        return imported;
    }

    internal async Task<FileImportBatchResult> ImportDesktopItemsIntoFolderAsync(
        IReadOnlyList<DesktopItemRef> items,
        string destinationFolderPath,
        bool isMove)
    {
        var paths = items
            .Select(item => item.FileSystemPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToArray();
        RegisterTargetedDesktopRefresh(paths);
        var result = await _fileOperations.ImportAsync(
            paths,
            destinationFolderPath,
            move: isMove);
        if (isMove && result.ImportedPaths.Count > 0)
        {
            var movedSources = result.SuccessfulItems
                .Select(item => Path.GetFullPath(item.SourcePath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var movedItemKeys = items
                .Where(item => item.FileSystemPath is not null &&
                    movedSources.Contains(Path.GetFullPath(item.FileSystemPath)))
                .Select(item => item.Key.ToString())
                .ToArray();
            await RefreshItemsCoreAsync(refreshSurfaces: false);
            _surfaceManager?.RefreshDesktopItemsRemoved(movedItemKeys);
            Changed?.Invoke(this, EventArgs.Empty);
            ScheduleSave();
        }
        return result;
    }

    internal async Task ReconcileExternalDesktopMoveAsync(
        IReadOnlyList<DesktopItemRef> items)
    {
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var movedPaths = items
            .Select(item => item.FileSystemPath)
            .Where(path => !string.IsNullOrWhiteSpace(path) && !File.Exists(path) && !Directory.Exists(path))
            .Select(path => path!)
            .ToArray();
        if (movedPaths.Length > 0)
        {
            await RefreshItemsCoreAsync(refreshSurfaces: false);
            _surfaceManager?.RefreshDesktopItemsRemoved(movedPaths);
            Changed?.Invoke(this, EventArgs.Empty);
            ScheduleSave();
        }
    }

    public async Task<FileImportBatchResult> TransferBoxItemsAsync(
        Guid sourceBoxId,
        IEnumerable<string> itemKeys,
        Guid targetBoxId,
        bool move)
    {
        if (sourceBoxId == targetBoxId)
        {
            return FileImportBatchResult.Empty;
        }
        var source = State.Boxes.First(candidate => candidate.Id == sourceBoxId);
        var target = State.Boxes.First(candidate => candidate.Id == targetBoxId);
        var keys = itemKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = GetItemsForBox(sourceBoxId).Where(item => keys.Contains(item.Key.ToString())).ToArray();
        if (items.Length == 0)
        {
            return FileImportBatchResult.Empty;
        }

        // Keep the runtime contract aligned with the drag surface. Callers
        // outside WinForms must not be able to turn a read-only mapping into a
        // filesystem move by passing move=true.
        if (source.MappedFolder?.IsReadOnly == true)
        {
            move = false;
        }

        if (!target.IsMappedFolder && !source.IsMappedFolder)
        {
            foreach (var item in items)
            {
                var itemKey = item.Key.ToString();
                State.Assignments[itemKey] = targetBoxId;
                MoveItemOrderKey(itemKey, targetBoxId);
            }
            NotifyBoxesItemsChanged([sourceBoxId, targetBoxId]);
            return FileImportBatchResult.Empty;
        }

        var paths = items.Select(item => item.FileSystemPath).OfType<string>().ToArray();
        if (paths.Length == 0)
        {
            return FileImportBatchResult.Empty;
        }
        var imported = await ImportFilesToBoxAsync(paths, targetBoxId, move);
        var sourceItemsChanged = false;
        if (move && !source.IsMappedFolder)
        {
            var movedSources = imported.SuccessfulItems
                .Select(result => Path.GetFullPath(result.SourcePath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var movedItems = items.Where(item => item.FileSystemPath is not null &&
                movedSources.Contains(Path.GetFullPath(item.FileSystemPath))).ToArray();
            foreach (var item in movedItems)
            {
                UnassignItemCore(item.Key.ToString());
            }
            if (movedItems.Length > 0)
            {
                sourceItemsChanged = true;
                await RefreshItemsCoreAsync(refreshSurfaces: false, applyDesktopRules: false);
                _surfaceManager?.RefreshDesktopItemsRemoved(
                    movedItems
                        .Where(item => item.FileSystemPath is not null)
                        .Select(item => item.FileSystemPath!)
                        .ToArray());
            }
        }
        if (source.IsMappedFolder)
        {
            await RefreshMappedFoldersAsync(false);
        }
        if (sourceItemsChanged || source.IsMappedFolder)
        {
            NotifyBoxesItemsChanged([sourceBoxId, targetBoxId]);
        }
        return imported;
    }

    public bool CanPasteIntoBox(DesktopBox box)
    {
        if (box.MappedFolder?.IsReadOnly == true)
        {
            return false;
        }
        try
        {
            return _fileOperations.HasClipboardFiles();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reports whether the clipboard currently holds files that can be pasted
    /// onto the replacement desktop surface. Only the offered formats are
    /// inspected: the low-level keyboard hook asks this before it consumes
    /// Ctrl+V, and reading the data object itself waits for the process that
    /// owns the clipboard while all system input stays queued behind the hook.
    /// </summary>
    public bool CanPasteToDesktop()
    {
        try
        {
            return _fileOperations.HasClipboardFiles();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return false;
        }
    }

    /// <summary>
    /// Pastes clipboard files into the real desktop folder. Name collisions are
    /// resolved by the import service's automatic numbering, so pasting a copy
    /// of an existing desktop file never raises a shell conflict prompt.
    /// </summary>
    public async Task<FileImportBatchResult> PasteToDesktopAsync()
    {
        await _pasteGate.WaitAsync();
        try
        {
            return await PasteToDesktopCoreAsync();
        }
        finally
        {
            _pasteGate.Release();
        }
    }

    private async Task<FileImportBatchResult> PasteToDesktopCoreAsync()
    {
        var clipboard = await _fileOperations.GetClipboardFilesAsync();
        if (!clipboard.HasFiles)
        {
            return FileImportBatchResult.Empty;
        }

        var desktopDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var paths = clipboard.Paths
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Where(path => DesktopPastePolicy.CanPasteSource(path, desktopDirectory, clipboard.Move))
            .ToArray();
        if (paths.Length == 0)
        {
            return FileImportBatchResult.Empty;
        }

        RegisterTargetedDesktopRefresh(paths.Append(desktopDirectory));
        var imported = await _fileOperations.ImportAsync(paths, desktopDirectory, clipboard.Move);
        if (imported.SucceededCount > 0)
        {
            RegisterTargetedDesktopRefresh(imported.ImportedPaths);
            await RefreshItemsCoreAsync(refreshSurfaces: false, applyDesktopRules: false);
            NotifyDesktopItemsAdded(imported.ImportedPaths);
        }
        if (clipboard.Move &&
            imported.FailedCount == 0 &&
            imported.SucceededCount == paths.Length)
        {
            await _fileOperations.ClearClipboardFilesAsync();
        }
        return imported;
    }

    /// <summary>
    /// Pastes clipboard files into a box. <paramref name="targetTabId"/> is the
    /// sub-tab currently shown by the box, so a pasted item lands in the view
    /// the user is looking at instead of being filtered out of it.
    /// </summary>
    public async Task<BoxPasteResult> PasteIntoBoxAsync(Guid boxId, Guid? targetTabId = null)
    {
        await _pasteGate.WaitAsync();
        try
        {
            return await PasteIntoBoxCoreAsync(boxId, targetTabId);
        }
        finally
        {
            _pasteGate.Release();
        }
    }

    private async Task<BoxPasteResult> PasteIntoBoxCoreAsync(Guid boxId, Guid? targetTabId)
    {
        var diagnosticStarted = Stopwatch.StartNew();
        var box = State.Boxes.First(candidate => candidate.Id == boxId);
        if (box.MappedFolder?.IsReadOnly == true)
        {
            throw new InvalidOperationException("此映射盒子已设为只读。");
        }
        var clipboard = await _fileOperations.GetClipboardFilesAsync();
        var clipboardElapsed = diagnosticStarted.ElapsedMilliseconds;
        if (!clipboard.HasFiles)
        {
            return new BoxPasteResult(0, FileImportBatchResult.Empty);
        }

        if (box.IsMappedFolder)
        {
            var mappedPaths = clipboard.Paths
                .Where(path => DesktopPastePolicy.CanPasteSource(
                    path,
                    box.MappedFolder!.Path,
                    clipboard.Move))
                .ToArray();
            if (mappedPaths.Length == 0)
            {
                return new BoxPasteResult(0, FileImportBatchResult.Empty);
            }

            var mappedImport = await ImportFilesToBoxAsync(mappedPaths, boxId, clipboard.Move);
            if (clipboard.Move &&
                mappedImport.FailedCount == 0 &&
                mappedImport.SucceededCount == mappedPaths.Length)
            {
                await _fileOperations.ClearClipboardFilesAsync();
            }
            return new BoxPasteResult(mappedImport.SucceededCount, mappedImport);
        }

        var desktopItems = Items
            .Where(item => item.FileSystemPath is not null)
            .ToDictionary(item => Path.GetFullPath(item.FileSystemPath!), StringComparer.OrdinalIgnoreCase);
        var external = new List<string>();
        var assignedKeys = new List<string>();
        // Only a cut moves an existing desktop item into the box. A copy has to
        // duplicate the file, otherwise pasting a copied box item just reassigns
        // it to the box it already belongs to and nothing appears.
        foreach (var path in clipboard.Paths)
        {
            var fullPath = Path.GetFullPath(path);
            if (clipboard.Move && desktopItems.TryGetValue(fullPath, out var item))
            {
                assignedKeys.Add(item.Key.ToString());
            }
            else if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                external.Add(fullPath);
            }
        }
        var manualTabId = ResolveManualTabTarget(box, targetTabId);
        var assigned = AssignItemsCore(
            assignedKeys,
            boxId,
            notify: external.Count == 0,
            targetTabId: manualTabId);
        var imported = FileImportBatchResult.Empty;
        if (external.Count > 0)
        {
            imported = await ImportFilesAsync(external, boxId, clipboard.Move, manualTabId);
            assigned += imported.SucceededCount;
            if (imported.SucceededCount == 0 && assignedKeys.Count > 0)
            {
                NotifyDesktopItemsAssignedToBox(boxId, assignedKeys);
            }
        }
        DiagnosticLog.Info(
            $"Box paste outer stages clipboardMs={clipboardElapsed} " +
            $"afterClipboardMs={diagnosticStarted.ElapsedMilliseconds - clipboardElapsed} " +
            $"totalMs={diagnosticStarted.ElapsedMilliseconds}");
        if (clipboard.Move &&
            assignedKeys.Count + imported.SucceededCount == clipboard.Paths.Count)
        {
            await _fileOperations.ClearClipboardFilesAsync();
        }
        return new BoxPasteResult(assigned, imported);
    }

    private static Guid? ResolveManualTabTarget(DesktopBox box, Guid? targetTabId) =>
        targetTabId is { } tabId && box.ManualTabs.Any(tab => tab.Id == tabId)
            ? tabId
            : null;

    public async Task RenameItemAsync(DesktopItemRef item, string newName, Guid? boxId = null)
    {
        var oldKey = item.Key.ToString();
        var oldPath = item.FileSystemPath is null ? null : Path.GetFullPath(item.FileSystemPath);
        var destination = await _fileOperations.RenameAsync(item, newName);
        if (oldPath is not null &&
            _originalFileAttributes.Remove(oldPath, out var originalAttributes) &&
            !string.IsNullOrWhiteSpace(destination))
        {
            _originalFileAttributes[Path.GetFullPath(destination)] = originalAttributes;
        }
        if (boxId is { } mappedBoxId &&
            State.Boxes.FirstOrDefault(box => box.Id == mappedBoxId)?.IsMappedFolder == true)
        {
            await RefreshMappedFoldersAsync(false);
            var renamedMapped = GetItemsForBox(mappedBoxId).FirstOrDefault(candidate =>
                candidate.FileSystemPath is not null &&
                string.Equals(Path.GetFullPath(candidate.FileSystemPath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase));
            if (renamedMapped is not null)
            {
                ReplaceItemOrderKey(oldKey, renamedMapped.Key.ToString());
            }
            NotifyBoxWorkspaceChanged(mappedBoxId);
            return;
        }

        await RefreshItemsCoreAsync(refreshSurfaces: false, applyDesktopRules: false);
        var renamed = Items.FirstOrDefault(candidate => candidate.FileSystemPath is not null &&
            string.Equals(Path.GetFullPath(candidate.FileSystemPath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase));
        if (renamed is not null && boxId is { } targetBoxId)
        {
            var newKey = renamed.Key.ToString();
            State.Assignments.Remove(oldKey);
            State.Assignments[newKey] = targetBoxId;
            ReplaceItemOrderKey(oldKey, newKey);
        }

        // The application owns this rename, so update its surfaces directly.
        // FileSystemWatcher notifications can be filtered, coalesced, or
        // arrive before the replacement snapshot is ready; relying on them
        // leaves geometry holding the old path even though the move succeeded.
        NotifyWorkspaceChanged(true);
    }

    private async Task RefreshItemsCoreAsync(bool refreshSurfaces = true, bool applyDesktopRules = true)
    {
        var diagnosticStarted = Stopwatch.StartNew();
        var items = await _itemProvider.EnumerateAsync();
        var enumerateElapsed = diagnosticStarted.ElapsedMilliseconds;
        // A failed or degraded enumeration (Explorer restart, cloud placeholder
        // lock, permission transition) must not wipe the persisted grouping or
        // make every desktop item disappear. Keep the previous snapshot when
        // the new one is empty and the desktop is known to have items.
        if (items.Count == 0 && _allDesktopItems.Count > 0)
        {
            DiagnosticLog.Info("Desktop enumeration returned no items; keeping previous snapshot.");
            items = _allDesktopItems;
        }
        MigrateAssignmentsForReplacedPaths(items);
        _allDesktopItems = items;
        Items = State.Settings.ShowSystemItems
            ? items
            : items.Where(item => !item.IsSystem || State.Assignments.ContainsKey(item.Key.ToString())).ToArray();
        var modelElapsed = diagnosticStarted.ElapsedMilliseconds - enumerateElapsed;
        await RefreshMappedFoldersAsync(false);
        var mappedElapsed = diagnosticStarted.ElapsedMilliseconds - enumerateElapsed - modelElapsed;
        if (applyDesktopRules && State.Organization.Enabled && State.Organization.RunOnDesktopChanges)
        {
            ApplyOrganizationRules(false);
        }
        if (refreshSurfaces)
        {
            _surfaceManager?.Refresh();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
        DiagnosticLog.Info(
            $"Refresh items stages enumerateMs={enumerateElapsed} modelMs={modelElapsed} " +
            $"mappedMs={mappedElapsed} tailMs={diagnosticStarted.ElapsedMilliseconds - enumerateElapsed - modelElapsed - mappedElapsed} " +
            $"totalMs={diagnosticStarted.ElapsedMilliseconds}");
    }

    public async Task RefreshItemsAsync(bool applyDesktopRules = true)
    {
        await RefreshItemsCoreAsync(refreshSurfaces: true, applyDesktopRules: applyDesktopRules);
    }

    // Documents saved "in place" via a temporary-file replace (Office/WPS do
    // this) get a new file identity, so their stable key changes while the
    // path stays the same. Without a migration the stored assignment (which
    // box the item belongs to) silently stops matching and the item drops
    // back onto the plain desktop. Rebind assignments that only changed
    // identity so the icon stays grouped after editing.
    private void MigrateAssignmentsForReplacedPaths(IReadOnlyList<DesktopItemRef> nextItems)
    {
        if (_allDesktopItems.Count == 0 || State.Assignments.Count == 0)
        {
            return;
        }
        var nextByPath = nextItems
            .Where(item => item.FileSystemPath is not null)
            .ToDictionary(
                item => Path.GetFullPath(item.FileSystemPath!).ToUpperInvariant(),
                StringComparer.Ordinal);
        foreach (var previous in _allDesktopItems)
        {
            if (previous.FileSystemPath is null)
            {
                continue;
            }
            var previousKey = previous.Key.ToString();
            if (!State.Assignments.TryGetValue(previousKey, out var boxId))
            {
                continue;
            }
            var pathKey = Path.GetFullPath(previous.FileSystemPath!).ToUpperInvariant();
            if (!nextByPath.TryGetValue(pathKey, out var next) ||
                string.Equals(next.Key.ToString(), previousKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            // Same path, new identity: move the assignment to the new key.
            State.Assignments.Remove(previousKey);
            State.Assignments[next.Key.ToString()] = boxId;
            ReplaceItemOrderKey(previousKey, next.Key.ToString());
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
            // 用户主动暂停：接管失败不再自动重试。
            _pausedByTakeoverFailure = false;
            StopTakeoverRetryTimer();
            AreDesktopItemsHidden = false;
            RestoreAssignedItemVisibility(true);
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

    public async Task SetShowHiddenFilesAsync(bool enabled)
    {
        State.Settings.ShowHiddenFiles = enabled;
        _itemProvider.ShowHiddenFiles = enabled;
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
            // Signature is informational: releases ship unsigned, so the update
            // chain relies on HTTPS plus the SHA-256SUMS digest. When a trusted
            // signature is present it still guards against publisher swaps.
            var signature = AuthenticodeVerifier.Verify(downloaded.InstallerPath);
            if (signature.IsTrusted &&
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

    public void SetAcrylicBoxes(bool enabled)
    {
        if (State.Settings.Appearance.UseAcrylicBoxes == enabled) return;
        State.Settings.Appearance.UseAcrylicBoxes = enabled;
        NotifyWorkspaceChanged(true);
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

    public void SetBoxBorderWidth(double value)
    {
        State.Settings.Appearance.BorderWidth = Math.Clamp(value, 1, 6);
        NotifyWorkspaceChanged(true);
    }

    public void SetBoxBorderColor(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "Auto" : value.Trim();
        if (!BoxBorderStyle.IsAuto(normalized) &&
            !BoxBorderStyle.TryParseColor(normalized, out _))
        {
            return;
        }
        State.Settings.Appearance.BorderColor = normalized;
        NotifyWorkspaceChanged(true);
    }

    public void SetBoxBorderOpacity(double value)
    {
        State.Settings.Appearance.BorderOpacity = Math.Clamp(value, 0, 100);
        NotifyWorkspaceChanged(true);
    }

    public void SetShowResizeGrip(bool enabled)
    {
        State.Settings.Appearance.ShowResizeGrip = enabled;
        NotifyWorkspaceChanged(true);
    }

    public void SetShowBoxScrollBar(bool enabled)
    {
        State.Settings.Appearance.ShowBoxScrollBar = enabled;
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
            box.IsCollapsed = enabled;
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

    public void SetIconLabelFontSize(double value)
    {
        State.Settings.Appearance.IconLabelFontSize = Math.Clamp(value, 6, 20);
        NotifyWorkspaceChanged(true);
    }

    public void SetIconLabelFontFamily(string value)
    {
        State.Settings.Appearance.IconLabelFontFamily =
            string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        NotifyWorkspaceChanged(true);
    }

    public void SetBoxBackground(Guid? boxId, string value)
    {
        foreach (var box in GetAppearanceTargets(boxId))
        {
            box.Appearance.Background = value;
        }
        NotifyBoxWorkspaceChanged(boxId);
    }

    public void SetBoxAccent(Guid? boxId, string value)
    {
        foreach (var box in GetAppearanceTargets(boxId))
        {
            box.Appearance.Accent = value;
        }
        NotifyBoxWorkspaceChanged(boxId);
    }

    public void SetBoxOpacity(Guid? boxId, double value)
    {
        foreach (var box in GetAppearanceTargets(boxId))
        {
            box.Appearance.Opacity = Math.Clamp(value, 0.35, 1);
        }
        NotifyBoxWorkspaceChanged(boxId);
    }

    public void SetBoxTitleBarHeight(Guid? boxId, double value)
    {
        foreach (var box in GetAppearanceTargets(boxId))
        {
            box.Appearance.TitleBarHeight = Math.Clamp(value, 32, 56);
        }
        LayoutCoordinator.NormalizeForMonitors(State, Monitors);
        NotifyBoxWorkspaceChanged(boxId);
    }

    public void SetBoxTitleColor(Guid? boxId, string value)
    {
        foreach (var box in GetAppearanceTargets(boxId))
        {
            box.Appearance.TitleColor = value;
        }
        NotifyBoxWorkspaceChanged(boxId);
    }

    public void SetBoxTitleFontSize(Guid? boxId, double value)
    {
        foreach (var box in GetAppearanceTargets(boxId))
        {
            box.Appearance.TitleFontSize = Math.Clamp(value, 8, 20);
        }
        NotifyBoxWorkspaceChanged(boxId);
    }

    public void SetBoxTitleFontFamily(Guid? boxId, string value)
    {
        var family = NormalizeFontFamily(value);
        foreach (var box in GetAppearanceTargets(boxId))
        {
            box.Appearance.TitleFontFamily = family;
        }
        NotifyBoxWorkspaceChanged(boxId);
    }

    public void SetBoxTitleFontBold(Guid? boxId, bool enabled)
    {
        foreach (var box in GetAppearanceTargets(boxId))
        {
            box.Appearance.TitleFontBold = enabled;
        }
        NotifyBoxWorkspaceChanged(boxId);
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
        RestoreAssignedItemVisibility(true);
        var primary = Monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? Monitors.FirstOrDefault();
        var disabledRules = LayoutCoordinator.ResetLayout(State, primary?.Id ?? "primary");
        _lastOrganizationAssignments = null;
        _lastOrganizationCreatedBoxes.Clear();
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

    public void ConfigureAiWebSearch(bool enabled, string apiKey)
    {
        var settings = State.Settings.AiClassification;
        settings.WebSearchEnabled = enabled;
        settings.WebSearchApiKey = apiKey ?? string.Empty;
        try
        {
            AiApiKeyStore.Save(GetAiWebSearchApiKeyPath(), settings.WebSearchApiKey);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Failed to save encrypted AI web search API key", exception);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public Task<IReadOnlyList<string>> GetAiModelsAsync(CancellationToken cancellationToken = default) =>
        _aiClassificationService.GetModelsAsync(State.Settings.AiClassification, cancellationToken);

    public Task TestAiModelConnectivityAsync(CancellationToken cancellationToken = default) =>
        _aiClassificationService.TestModelConnectivityAsync(State.Settings.AiClassification, cancellationToken);

    public AiClassificationWorkspace GetAiClassificationWorkspace() => new(
        _workspaceRevision,
        GetAiClassificationWorkspaceItems());

    public async Task<AiClassificationPreview> PreviewAiClassificationAsync(
        IProgress<AiClassificationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        IProgress<string>? modelOutput = null,
        IProgress<AiClassificationModelStreamUpdate>? modelStream = null,
        IProgress<AiClassificationUsageProgress>? usageProgress = null,
        IProgress<AiClassificationTransportProgress>? transportProgress = null,
        IProgress<AiWebSearchProgress>? webSearchProgress = null,
        IProgress<AiClassificationActivity>? activityProgress = null)
    {
        var workspace = GetAiClassificationWorkspace();
        return await PreviewAiClassificationAsync(
                workspace.WorkspaceRevision,
                workspace.Items.Select(item => item.ItemKey).ToArray(),
                progress,
                cancellationToken,
                modelOutput,
                modelStream,
                usageProgress,
                transportProgress,
                webSearchProgress,
                activityProgress)
            .ConfigureAwait(false);
    }

    public async Task<AiClassificationPreview> PreviewAiClassificationAsync(
        long expectedWorkspaceRevision,
        IReadOnlyCollection<string> selectedItemKeys,
        IProgress<AiClassificationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        IProgress<string>? modelOutput = null,
        IProgress<AiClassificationModelStreamUpdate>? modelStream = null,
        IProgress<AiClassificationUsageProgress>? usageProgress = null,
        IProgress<AiClassificationTransportProgress>? transportProgress = null,
        IProgress<AiWebSearchProgress>? webSearchProgress = null,
        IProgress<AiClassificationActivity>? activityProgress = null) =>
        await RunAiOrganizationAsync(async operationToken =>
        {
            if (expectedWorkspaceRevision != _workspaceRevision)
            {
                throw new InvalidOperationException("桌面状态已变化，请刷新项目后重新生成 AI 预览。");
            }

            var settings = State.Settings.AiClassification;
            var labels = ParseAiCategoryLabels(settings.CategoryLabels);
            if (labels.Count == 0)
            {
                throw new InvalidOperationException("请至少提供一个分类标签。");
            }

            var workspace = GetAiClassificationWorkspace();
            var knownKeys = workspace.Items
                .Select(item => item.ItemKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var requestedKeys = selectedItemKeys
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!requestedKeys.IsSubsetOf(knownKeys))
            {
                throw new InvalidOperationException("桌面项目已变化，请刷新项目后重新生成 AI 预览。");
            }

            var selectedItems = AiClassificationWorkbench.Select(workspace, requestedKeys);
            var candidates = selectedItems
                .Select(item => new AiClassificationInput(item.ItemKey, item.DisplayName))
                .ToArray();
            var usageAccumulator = new AiClassificationUsageAccumulator(usageProgress);
            DiagnosticLog.Info(
                $"AI classification preview started endpoint={GetAiDiagnosticEndpoint(settings.BaseUrl)} " +
                $"model={settings.Model.Trim()} candidates={candidates.Length} labels={labels.Count}");
            if (candidates.Length == 0)
            {
                progress?.Report(new AiClassificationProgress(
                    0,
                    0,
                    0,
                    0,
                    false,
                    "没有需要分类的桌面图标"));
                return new AiClassificationPreview(_workspaceRevision, 0, [], [])
                {
                    RequestedItemKeys = []
                };
            }

            var classifications = new List<AiClassificationAssignment>();
            var totalBatches = (candidates.Length + AiClassificationService.MaxItemsPerRequest - 1) /
                AiClassificationService.MaxItemsPerRequest;
            var completedItems = 0;
            var completedBatches = 0;
            var remainingWebSearchItems = TavilySearchService.MaxItemsPerRun;
            progress?.Report(new AiClassificationProgress(
                completedItems,
                candidates.Length,
                completedBatches,
                totalBatches,
                true,
                "正在准备分类项目"));
            foreach (var batch in candidates.Chunk(AiClassificationService.MaxItemsPerRequest))
            {
                operationToken.ThrowIfCancellationRequested();
                progress?.Report(new AiClassificationProgress(
                    completedItems,
                    candidates.Length,
                    completedBatches,
                    totalBatches,
                    true,
                    "正在请求 AI 分类"));
                IProgress<AiClassificationTransportProgress>? transportProgressRelay = transportProgress is null
                    ? null
                    : new Progress<AiClassificationTransportProgress>(transport =>
                    {
                        DiagnosticLog.Info(
                            $"AI classification transport attempt={transport.Attempt}/{transport.TotalAttempts} " +
                            $"fallback={transport.IsCompatibilityFallback} streaming={transport.IsStreaming}");
                        transportProgress.Report(transport);
                        if (!transport.IsCompatibilityFallback)
                        {
                            return;
                        }
                        progress.Report(new AiClassificationProgress(
                            completedItems,
                            candidates.Length,
                            completedBatches,
                            totalBatches,
                            true,
                            "接口不支持当前输出能力，正在切换兼容模式"));
                    });
                var reasoningChunks = 0;
                var reasoningCharacters = 0;
                var contentChunks = 0;
                var contentCharacters = 0;
                var batchNumber = completedBatches + 1;
                foreach (var item in batch)
                {
                    activityProgress?.Report(new AiClassificationActivity(
                        item.ItemKey, item.DisplayName, AiClassificationActivityPhase.Analyzing));
                }
                var streamProgress = new DirectProgress<AiClassificationModelStreamUpdate>(update =>
                {
                    var (chunkCount, characterCount) = update.Kind switch
                    {
                        AiClassificationModelStreamKind.Reasoning =>
                            (reasoningChunks = checked(reasoningChunks + 1),
                                reasoningCharacters = checked(reasoningCharacters + update.Text.Length)),
                        _ =>
                            (contentChunks = checked(contentChunks + 1),
                                contentCharacters = checked(contentCharacters + update.Text.Length))
                    };
                    if (chunkCount == 1 || chunkCount % 128 == 0)
                    {
                        DiagnosticLog.Info(
                            $"AI classification stream batch={batchNumber}/{totalBatches} " +
                            $"kind={update.Kind} chunks={chunkCount} characters={characterCount}");
                    }
                    modelStream?.Report(update);
                });
                IReadOnlyList<AiClassificationAssignment> result;
                try
                {
                    result = await _aiClassificationService.ClassifyAsync(
                        settings,
                        batch,
                        labels,
                    operationToken,
                    modelOutput,
                    transportProgressRelay,
                    streamProgress,
                    usageAccumulator).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    DiagnosticLog.Error(
                        $"AI classification request failed endpoint={GetAiDiagnosticEndpoint(settings.BaseUrl)} " +
                        $"model={settings.Model.Trim()} batch={completedBatches + 1}/{totalBatches}",
                        exception);
                    throw;
                }

                var webSearchCandidates = settings.WebSearchEnabled &&
                    !string.IsNullOrWhiteSpace(settings.WebSearchApiKey)
                    ? AiClassificationFallbackPlanner.SelectForWebSearch(
                        batch,
                        result,
                        remainingWebSearchItems)
                    : [];
                if (webSearchCandidates.Count > 0)
                {
                    remainingWebSearchItems -= webSearchCandidates.Count;
                    progress?.Report(new AiClassificationProgress(
                        completedItems,
                        candidates.Length,
                        completedBatches,
                        totalBatches,
                        true,
                        "正在联网辅助识别待确认项目"));
                    webSearchProgress?.Report(new AiWebSearchProgress(
                        AiWebSearchPhase.Started,
                        webSearchCandidates.Count,
                        0));
                    var webSearchStopwatch = Stopwatch.StartNew();
                    try
                    {
                        var evidenceItems = await _tavilySearchService.SearchAsync(
                                settings.WebSearchApiKey,
                                webSearchCandidates,
                                operationToken)
                            .ConfigureAwait(false);
                        webSearchProgress?.Report(new AiWebSearchProgress(
                            AiWebSearchPhase.Completed,
                            webSearchCandidates.Count,
                            evidenceItems.Count,
                            Message: $"{webSearchStopwatch.Elapsed.TotalSeconds:0.0}s"));
                        if (evidenceItems.Count > 0)
                        {
                            var supplementalAssignments = await _aiClassificationService.ClassifyAsync(
                                    settings,
                                    evidenceItems,
                                    labels,
                                    operationToken,
                                    modelOutput,
                                    transportProgressRelay,
                                    streamProgress,
                                    usageAccumulator)
                                .ConfigureAwait(false);
                            result = result
                                .Concat(supplementalAssignments)
                                .GroupBy(assignment => assignment.ItemKey, StringComparer.Ordinal)
                                .Select(group => group.First())
                                .ToArray();
                        }
                    }
                    catch (AiWebSearchRequestException exception)
                    {
                        DiagnosticLog.Error(
                            $"AI web search failed provider=Tavily candidates={webSearchCandidates.Count}",
                            exception);
                        webSearchProgress?.Report(new AiWebSearchProgress(
                            AiWebSearchPhase.Failed,
                            webSearchCandidates.Count,
                            0,
                            Message: exception.TechnicalMessage));
                        progress?.Report(new AiClassificationProgress(
                            completedItems,
                            candidates.Length,
                            completedBatches,
                            totalBatches,
                            true,
                            AiWebSearchRequestException.SafeMessage));
                    }
                }
                var assignmentsByKey = result.ToDictionary(item => item.ItemKey, StringComparer.OrdinalIgnoreCase);
                foreach (var item in batch)
                {
                    if (assignmentsByKey.TryGetValue(item.ItemKey, out var assignment))
                    {
                        activityProgress?.Report(new AiClassificationActivity(
                            item.ItemKey, item.DisplayName, AiClassificationActivityPhase.Classified, assignment.Label));
                    }
                    else
                    {
                        activityProgress?.Report(new AiClassificationActivity(
                            item.ItemKey, item.DisplayName, AiClassificationActivityPhase.Uncertain));
                    }
                    // Give the UI a beat between rows so the agent timeline reads as a
                    // continuous classification flow instead of one bulk repaint.
                    await Task.Delay(45, operationToken).ConfigureAwait(false);
                }
                classifications.AddRange(result);
                completedItems += batch.Length;
                completedBatches++;
                progress?.Report(new AiClassificationProgress(
                    completedItems,
                    candidates.Length,
                    completedBatches,
                    totalBatches,
                    false,
                    "已完成一批分类"));
            }

            progress?.Report(new AiClassificationProgress(
                candidates.Length,
                candidates.Length,
                totalBatches,
                totalBatches,
                true,
                "正在生成整理预览"));

            var existingBoxTitles = State.Boxes
                .Where(box => !box.IsMappedFolder)
                .Select(box => box.Title.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var newBoxLabels = classifications
                .Select(classification => classification.Label)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(label => !existingBoxTitles.Contains(label))
                .ToArray();
            return new AiClassificationPreview(
                _workspaceRevision,
                candidates.Length,
                classifications,
                newBoxLabels)
            {
                RequestedItemKeys = candidates.Select(candidate => candidate.ItemKey).ToArray()
            };
        }, cancellationToken).ConfigureAwait(false);

    public async Task<AiClassificationApplyResult> ApplyAiClassificationPreviewAsync(
        AiClassificationPreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return await RunAiOrganizationAsync(operationToken =>
        {
            if (preview.WorkspaceRevision != _workspaceRevision)
            {
                throw new InvalidOperationException("桌面状态已变化，请重新预览 AI 整理结果。");
            }
            ValidateAiClassificationPreviewScope(preview);
            operationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ApplyAiClassificationPreviewCore(preview, operationToken));
        }, cancellationToken).ConfigureAwait(false);
    }

    [Obsolete("Use PreviewAiClassificationAsync followed by ApplyAiClassificationPreviewAsync so the user can confirm the result.")]
    public async Task<AiClassificationApplyResult> ApplyAiClassificationAsync(
        CancellationToken cancellationToken = default)
    {
        var preview = await PreviewAiClassificationAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return await ApplyAiClassificationPreviewAsync(preview, cancellationToken).ConfigureAwait(false);
    }

    public void CancelAiOrganization()
    {
        _aiOrganizationGate.Cancel();
    }

    private AiClassificationApplyResult ApplyAiClassificationPreviewCore(
        AiClassificationPreview preview,
        CancellationToken cancellationToken)
    {
        if (preview.Assignments.Count == 0)
        {
            return new AiClassificationApplyResult(preview.Requested, 0, 0, 0, preview.Requested, []);
        }

        var itemsByKey = Items.ToDictionary(item => item.Key.ToString(), StringComparer.OrdinalIgnoreCase);
        var assignmentsToApply = preview.Assignments
            .Where(classification => itemsByKey.ContainsKey(classification.ItemKey))
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (assignmentsToApply.Length == 0)
        {
            return new AiClassificationApplyResult(
                preview.Requested,
                preview.Assignments.Count,
                0,
                0,
                preview.Requested - preview.Assignments.Count,
                preview.Assignments);
        }

        // Applying a confirmed preview is deliberately atomic: cancellation is
        // observed before state mutation, not between individual assignments.
        _lastOrganizationAssignments = new Dictionary<string, Guid>(
            State.Assignments,
            StringComparer.OrdinalIgnoreCase);
        _lastOrganizationCreatedBoxes = [];
        var boxesByTitle = State.Boxes
            .Where(box => !box.IsMappedFolder)
            .GroupBy(box => box.Title.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var applied = 0;
        foreach (var classification in assignmentsToApply)
        {
            if (!boxesByTitle.TryGetValue(classification.Label, out var box))
            {
                box = CreateBoxCore(classification.Label);
                boxesByTitle[classification.Label] = box;
                _lastOrganizationCreatedBoxes.Add(box.Id);
            }
            State.Assignments[classification.ItemKey] = box.Id;
            MoveItemOrderKey(classification.ItemKey, box.Id);
            applied++;
        }
        if (applied > 0 || _lastOrganizationCreatedBoxes.Count > 0)
        {
            if (IsPaused)
            {
                _workspaceRevision++;
                Changed?.Invoke(this, EventArgs.Empty);
                ScheduleSave();
            }
            else
            {
                NotifyWorkspaceChanged(true);
            }
        }
        return new AiClassificationApplyResult(
            preview.Requested,
            preview.Assignments.Count,
            applied,
            _lastOrganizationCreatedBoxes.Count,
            preview.Requested - preview.Assignments.Count,
            preview.Assignments);
    }

    private async Task<T> RunAiOrganizationAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken) =>
        await _aiOrganizationGate.ExecuteAsync(operation, cancellationToken).ConfigureAwait(false);

    private static IReadOnlyList<string> ParseAiCategoryLabels(string value) => value
        .Split(['\r', '\n', ',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(label => label.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private string GetAiApiKeyPath() => Path.Combine(ConfigDirectory, "ai-api-key.dat");

    private string GetAiWebSearchApiKeyPath() => Path.Combine(ConfigDirectory, "ai-web-search-key.dat");

    private static string GetAiDiagnosticEndpoint(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var uri))
        {
            return "<invalid>";
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private IReadOnlyList<AiClassificationWorkspaceItem> GetAiClassificationWorkspaceItems() => Items
        .Where(item => item.Kind != DesktopItemKind.Shell)
        .Where(item => !State.Assignments.ContainsKey(item.Key.ToString()))
        .Select(item => new AiClassificationWorkspaceItem(
            item.Key.ToString(),
            item.DisplayName,
            item.Kind,
            item.ParsingName))
        .ToArray();

    private void ValidateAiClassificationPreviewScope(AiClassificationPreview preview)
    {
        var requestedKeys = preview.RequestedItemKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requestedKeys.Count != preview.Requested)
        {
            throw new InvalidOperationException("AI 预览范围无效，请重新生成预览。");
        }

        var eligibleKeys = GetAiClassificationWorkspaceItems()
            .Select(item => item.ItemKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var labels = ParseAiCategoryLabels(State.Settings.AiClassification.CategoryLabels)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in preview.Assignments)
        {
            if (!requestedKeys.Contains(assignment.ItemKey) ||
                !eligibleKeys.Contains(assignment.ItemKey) ||
                !labels.Contains(assignment.Label))
            {
                throw new InvalidOperationException("AI 预览包含无效分类，请重新生成预览。");
            }
        }
    }

    public OrganizationApplyResult ApplyOrganizationRules(bool notify = true)
    {
        var createdBoxIds = EnsureSmartOrganizationStructure();
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
            // Boxes this run created go with the undo; restoring the old
            // assignments alone would leave them behind empty.
            _lastOrganizationCreatedBoxes = createdBoxIds;
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

    // Rule-driven assignment hides each newly assigned item from Explorer's
    // desktop view so the desktop does not show a duplicate of the same item.
    public OrganizationApplyResult SmartOrganize()
    {
        var currentKeys = Items.Select(item => item.Key.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var staleKey in State.Assignments.Keys.Where(key => !currentKeys.Contains(key)).ToArray())
        {
            State.Assignments.Remove(staleKey);
            MoveItemOrderKey(staleKey, null);
        }
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
        var createdBoxIds = _lastOrganizationCreatedBoxes;
        _lastOrganizationCreatedBoxes = [];

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
        foreach (var box in State.Boxes.Where(box => createdBoxIds.Contains(box.Id)).ToArray())
        {
            if (!State.Assignments.Values.Contains(box.Id))
            {
                State.Boxes.Remove(box);
                // Unpin the rules routed into this box so they go back to
                // "create on organize" instead of pointing at a missing target.
                foreach (var rule in State.OrganizationRules.Where(rule => rule.TargetBoxId == box.Id))
                {
                    rule.TargetBoxId = null;
                }
            }
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
        NotifyBoxWorkspaceChanged(boxId);
    }

    public void SetBoxLabelFontSize(Guid? boxId, double value)
    {
        foreach (var box in GetAppearanceTargets(boxId))
        {
            box.Appearance.LabelFontSize = Math.Clamp(value, 8, 16);
        }
        NotifyBoxWorkspaceChanged(boxId);
    }

    public void SetBoxLabelFontFamily(Guid? boxId, string value)
    {
        var family = NormalizeFontFamily(value);
        foreach (var box in GetAppearanceTargets(boxId))
        {
            box.Appearance.LabelFontFamily = family;
        }
        NotifyBoxWorkspaceChanged(boxId);
    }

    public void SetBoxShowItemLabels(Guid? boxId, bool enabled)
    {
        foreach (var box in GetAppearanceTargets(boxId))
        {
            box.Appearance.ShowItemLabels = enabled;
        }
        NotifyBoxWorkspaceChanged(boxId);
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
        NotifyBoxWorkspaceChanged(boxId);
    }

    public void SetBoxSortMode(Guid? boxId, BoxSortMode mode)
    {
        foreach (var box in GetAppearanceTargets(boxId))
        {
            box.SortMode = mode;
        }
        NotifyBoxWorkspaceChanged(boxId);
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

    public void RequestShowSettings(string? page = null) =>
        ShowSettingsRequested?.Invoke(this, new ShowSettingsRequestedEventArgs(page));
    public void RequestAiOrganization() => AiOrganizationRequested?.Invoke(this, EventArgs.Empty);
    public void RequestExit() => ExitRequested?.Invoke(this, EventArgs.Empty);

    public void NotifyMinimizedToTray()
    {
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        // Keep the caller stack so an unexpected shutdown can be traced back
        // to its trigger (settings change, window close, update, crash path).
        DiagnosticLog.Info(
            "Runtime disposal started\n" + Environment.StackTrace);
        _hostTimer.Stop();
        _uiHeartbeatTimer.Stop();
        _saveTimer.Stop();
        _desktopZoomTimer.Stop();
        _desktopViewRefreshTimer.Stop();
        _desktopMenuRefreshTimer.Stop();
        CancelAiOrganization();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        try
        {
            _surfaceManager?.Dispose();
        }
        finally
        {
            _surfaceManager = null;
            RestoreAssignedItemVisibility(true);
            EnsureDesktopInput("dispose");
        }
        _itemProvider.Dispose();
        _mappedFolderProvider.Dispose();
        _hotkeyService.Pressed -= OnGlobalHotkeyPressed;
        _hotkeyService.Dispose();
        try
        {
            _desktopContextMenu.SetEnabled(false, string.Empty);
        }
        catch
        {
        }
        if (_desktopInputMonitor is not null)
        {
            _desktopInputMonitor.IconZoomRequested -= OnDesktopIconZoomRequested;
            _desktopInputMonitor.BoxDragMouseWheelRequested -= OnBoxDragMouseWheelRequested;
            _desktopInputMonitor.DesktopSurfaceClicked -= OnDesktopSurfaceClicked;
            _desktopInputMonitor.DesktopContextMenuRequested -= OnDesktopContextMenuRequested;
            _desktopInputMonitor.DesktopContextMenuCommandRequested -= OnDesktopContextMenuCommandRequested;
            _desktopInputMonitor.DesktopContextMenuRefreshRequested -= OnDesktopContextMenuRefreshRequested;
            _desktopInputMonitor.Dispose();
        }
        _updateCancellation.Cancel();
        if (_updateLock.Wait(0))
        {
            _updateService.Dispose();
            _updateLock.Release();
            _updateCancellation.Dispose();
        }
        _menuFont.Dispose();
        _aiOrganizationGate.Dispose();
        _aiClassificationService.Dispose();
        _tavilySearchService.Dispose();
        _iconProvider.ClearCache();
        SaveNowAsync().GetAwaiter().GetResult();
        _saveLock.Dispose();
        _mappedRefreshLock.Dispose();
        _pasteGate.Dispose();
        _desktopZoomTimer.Dispose();
        _desktopViewRefreshTimer.Dispose();
        _desktopMenuRefreshTimer.Dispose();
        _uiHeartbeatTimer.Dispose();
        _takeoverRetryTimer.Dispose();
        DiagnosticLog.Info("Runtime disposal completed");
    }

    private void StartTakeover()
    {
        DiagnosticLog.Info($"Desktop takeover starting listView=0x{_desktopHost.DesktopListView.ToInt64():X} items={_allDesktopItems.Count}");
        IsPaused = false;
        AreDesktopItemsHidden = false;
        if (ActivateDesktopSurfaces("startup"))
        {
            _surfaceManager?.SetDesktopIconsVisible(_desktopIconsVisible);
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
            _pausedByTakeoverFailure = false;
            _takeoverRetryAttempt = 0;
            StopTakeoverRetryTimer();
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
            AreDesktopItemsHidden = false;
            RestoreAssignedItemVisibility(true);
            EnsureDesktopInput($"takeover rollback ({context})");
            // 第三方桌面软件（画报、桌面助手等）可能短暂抢占桌面宿主后恢复；
            // 按退避计划自动重试接管，避免停留在「桌面未连接」等手动恢复。
            _pausedByTakeoverFailure = true;
            ScheduleTakeoverRetry(context);
            return false;
        }
    }

    private void ScheduleTakeoverRetry(string context)
    {
        if (_takeoverRetryAttempt >= TakeoverRetryDelays.Length)
        {
            DiagnosticLog.Info(
                $"Takeover auto-retry exhausted attempts={_takeoverRetryAttempt} context={context}");
            return;
        }
        var delay = TakeoverRetryDelays[_takeoverRetryAttempt];
        _takeoverRetryAttempt++;
        DiagnosticLog.Info(
            $"Takeover auto-retry scheduled delayMs={delay.TotalMilliseconds} attempt={_takeoverRetryAttempt} context={context}");
        _takeoverRetryTimer.Start(delay);
    }

    private void OnTakeoverRetryTick()
    {
        RetryTakeoverAfterFailure($"auto retry {_takeoverRetryAttempt}");
    }

    private bool RetryTakeoverAfterFailure(string context)
    {
        if (!_pausedByTakeoverFailure || !State.Settings.TakeOverDesktop)
        {
            return false;
        }
        IsPaused = false;
        AreDesktopItemsHidden = false;
        if (ActivateDesktopSurfaces(context))
        {
            DiagnosticLog.Info($"Desktop takeover auto-recovered context={context}");
            return true;
        }
        return false;
    }

    private void StopTakeoverRetryTimer()
    {
        _takeoverRetryTimer.Stop();
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
            _surfaceManager.SetDesktopIconsVisible(_desktopIconsVisible);
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
            TitleColor = source.TitleColor,
            TitleFontFamily = source.TitleFontFamily,
            TitleFontSize = source.TitleFontSize,
            TitleFontBold = source.TitleFontBold
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
            box.IsCollapsed = true;
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

    private HashSet<Guid> EnsureSmartOrganizationStructure()
    {
        var monitor = Monitors.FirstOrDefault(candidate => candidate.IsPrimary) ?? Monitors.First();
        var active = new List<(DesktopBox Box, int ItemCount)>();
        var createdBoxIds = new HashSet<Guid>();
        // Size each rule by what the engine will actually hand it, not by what
        // the rule matches on its own: an item that a higher-priority rule
        // claims first, or that already sits in another box, never arrives,
        // and a box created for it would stay empty.
        var decisions = _organizationRuleEngine.Preview(State, Items, State.Organization.ReassignExistingItems);
        // Assignments whose desktop item was deleted survive in state, so every
        // "is this box empty / how big should it be" question below has to be
        // asked against the items that actually exist. Counting raw
        // assignments kept a visibly empty box alive.
        var liveItemKeys = Items.Select(item => item.Key.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in BuiltInOrganizationRules.Definitions)
        {
            var rule = State.OrganizationRules.FirstOrDefault(candidate =>
                string.Equals(candidate.BuiltInId, definition.Id, StringComparison.OrdinalIgnoreCase));
            if (rule is null)
            {
                continue;
            }
            var box = OrganizationRuleEngine.ResolveTargetBox(State, rule);
            var itemCount = rule.Enabled && rule.Action == OrganizationRuleAction.AssignToBox
                ? OrganizationRuleEngine.CountProjectedItems(decisions, State.Assignments, rule.Id, box?.Id, liveItemKeys)
                : 0;

            if (itemCount == 0)
            {
                if (box is not null && OrganizationRuleEngine.CanRemoveEmptyBox(State, box, rule, liveItemKeys))
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
                    StackOrder = BoxStacking.GetFrontStackOrder(State.Boxes, monitor.Id),
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
                active.Add((box, itemCount));
            }
        }

        foreach (var rule in State.OrganizationRules.Where(rule =>
                     rule.Enabled && rule.Action == OrganizationRuleAction.AssignToBox &&
                     string.IsNullOrWhiteSpace(rule.BuiltInId)).ToArray())
        {
            var box = OrganizationRuleEngine.ResolveTargetBox(State, rule);
            var itemCount = OrganizationRuleEngine.CountProjectedItems(
                decisions,
                State.Assignments,
                rule.Id,
                box?.Id,
                liveItemKeys);
            if (itemCount == 0)
            {
                // Same cleanup the built-in rules get: a box this pass owns and
                // that no longer has anything to collect must not be left
                // sitting empty on the desktop. Without this, deleting the
                // files a custom rule matched left its box behind forever.
                if (box is not null && OrganizationRuleEngine.CanRemoveEmptyBox(State, box, rule, liveItemKeys))
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
                    StackOrder = BoxStacking.GetFrontStackOrder(State.Boxes, monitor.Id),
                    IsAutoGenerated = true
                };
                State.Boxes.Add(box);
                createdBoxIds.Add(box.Id);
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
                active.Add((box, itemCount));
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
        return createdBoxIds;
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
        string.IsNullOrWhiteSpace(value) ? BoxAppearance.DefaultFontFamily : value.Trim();

    private void NotifyWorkspaceChanged(bool rebuild)
    {
        _workspaceRevision++;
        try
        {
            if (rebuild)
            {
                if (_surfaceManager is not null &&
                    _surfaceManager.AcrylicRequested != State.Settings.Appearance.UseAcrylicBoxes)
                {
                    ActivateDesktopSurfaces("acrylic preference changed");
                }
                else
                {
                    _surfaceManager?.Refresh();
                }
            }
            else
            {
                _surfaceManager?.UpdateRegions();
            }
        }
        catch (Exception exception)
        {
            // A failure to refresh a surface must never take down the whole
            // process from a settings setter; log it and keep the state
            // save flowing so the user's choice still persists.
            DiagnosticLog.Error("Desktop surface refresh failed after a workspace change", exception);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    private async Task RefreshMappedFoldersAsync(bool notify = true)
    {
        var changed = false;
        var changedBoxIds = new HashSet<Guid>();
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
                changedBoxIds.Add(staleId);
            }

            foreach (var box in mappedBoxes)
            {
                var snapshot = await _mappedFolderProvider.EnumerateAsync(box.MappedFolder!.Path);
                var boxChanged = !_mappedFolderSnapshots.TryGetValue(box.Id, out var previous) ||
                    !MappedSnapshotsEqual(previous, snapshot);
                changed |= boxChanged;
                if (boxChanged)
                {
                    changedBoxIds.Add(box.Id);
                }
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
            _surfaceManager?.RefreshBoxes(changedBoxIds);
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
            // Explorer is still publishing Shell notifications for a file
            // operation CrabDesk just completed. Probing and potentially
            // rebuilding its child-window host during that interval can race
            // the Shell's own desktop refresh.
            if (DateTimeOffset.Now <= _targetedDesktopRefreshExpiresAt)
            {
                return;
            }
            var probeStarted = Stopwatch.StartNew();
            var healthSnapshot = await Task.Run(() =>
            {
                var nativeProbe = Stopwatch.StartNew();
                var host = DesktopHostService.Probe();
                var systemIconVisibilitySignature =
                    DesktopItemProvider.GetSystemDesktopIconVisibilitySignature();
                var monitors = _monitorService.GetMonitors();
                return (
                    Host: host,
                    SystemIconVisibilitySignature: systemIconVisibilitySignature,
                    Monitors: monitors,
                    NativeMs: nativeProbe.ElapsedMilliseconds);
            });
            if (probeStarted.ElapsedMilliseconds >= 100)
            {
                // nativeMs is the probe itself. Anything beyond it is pool
                // queueing plus the wait for the UI thread to resume this await,
                // so only nativeMs indicts Explorer.
                DiagnosticLog.Info(
                    $"Host health probe timing elapsedMs={probeStarted.ElapsedMilliseconds} " +
                    $"nativeMs={healthSnapshot.NativeMs}");
            }
            // The background probe is immutable. Apply all three related
            // handles together on the UI thread so surfaces never observe a
            // partially updated Explorer host.
            var hostChanged = _desktopHost.Apply(healthSnapshot.Host);
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
            // Sort, zoom, and visibility synchronization is armed by the
            // corresponding desktop input events. The health tick must not poll
            // Explorer's automation view while it is processing file changes.
            var desktopViewChanged = false;
            var systemIconVisibilitySignature = healthSnapshot.SystemIconVisibilitySignature;
            var systemIconVisibilityChanged = !string.Equals(
                _desktopSystemIconVisibilitySignature,
                systemIconVisibilitySignature,
                StringComparison.Ordinal);
            if (systemIconVisibilityChanged)
            {
                _desktopSystemIconVisibilitySignature = systemIconVisibilitySignature;
                await RefreshItemsAsync(false);
                DiagnosticLog.Info("Windows desktop system-icon visibility synchronized.");
            }
            var monitors = healthSnapshot.Monitors;
            var topologyChanged = !monitors.Select(monitor =>
                    $"{monitor.Id}:{monitor.PixelBounds}:{monitor.PixelWorkArea}:{monitor.DpiScale}")
                .SequenceEqual(Monitors.Select(monitor =>
                    $"{monitor.Id}:{monitor.PixelBounds}:{monitor.PixelWorkArea}:{monitor.DpiScale}"));
            if (topologyChanged)
            {
                DiagnosticLog.Info(
                    $"Monitor topology changed new={monitors.Count} " +
                    $"old={Monitors.Count} " +
                    string.Join("|", monitors.Select(monitor => $"{monitor.Id}:{monitor.PixelBounds}:{monitor.PixelWorkArea}")));
            }
            if (!hostChanged && !topologyChanged)
            {
                if (!IsPaused &&
                    !TryApplyPendingDesktopSort(sortChanged: false, hasLiveSortColumns: false) &&
                    desktopViewChanged)
                {
                    RefreshDesktopView();
                }
                return;
            }

            Monitors = monitors;
            NormalizeMonitorIds();
            LayoutCoordinator.NormalizeForMonitors(State, Monitors);
            if (!IsPaused)
            {
                ActivateDesktopSurfaces("host refresh");
            }
            else if (hostChanged)
            {
                // 宿主刚从第三方抢占中恢复：立即重试接管，不等退避计时器。
                RetryTakeoverAfterFailure("host refresh recovery");
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

    private void OnUiHeartbeat()
    {
        UiThreadWatchdog.ReportAlive();
        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Exchange(ref _lastUiHeartbeatTimestamp, now);
        if (previous == 0)
        {
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(previous, now);
        if (elapsed >= TimeSpan.FromMilliseconds(750))
        {
            // poolIntervalMs isolates thread-pool starvation; dispatchMs is the
            // time this tick spent queued for the UI thread and is the only one
            // of the two that means the UI thread itself was blocked.
            var latency = _lastUiHeartbeatLatency;
            DiagnosticLog.Info(
                $"UI heartbeat delayed elapsedMs={elapsed.TotalMilliseconds:0} " +
                $"poolIntervalMs={latency.PoolIntervalMs:0} " +
                $"dispatchMs={latency.DispatchMs:0}");
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
        BeginInvoke("global hotkey", () =>
        {
            try
            {
                if (eventArgs.Action == HotkeyAction.ShowDesktop)
                {
                    DesktopWindowTools.ToggleDesktop();
                    return;
                }

                SmartOrganize();
            }
            catch (Exception exception)
            {
                DiagnosticLog.Error("Hotkey smart organize failed", exception);
            }
        });
    }

    private void ConfigureDesktopInputMonitor()
    {
        if (_desktopInputMonitor is null)
        {
            _desktopInputMonitor = new DesktopInputMonitor();
            _desktopInputMonitor.IconZoomRequested += OnDesktopIconZoomRequested;
            _desktopInputMonitor.BoxDragMouseWheelRequested += OnBoxDragMouseWheelRequested;
            _desktopInputMonitor.DesktopSurfaceClicked += OnDesktopSurfaceClicked;
            _desktopInputMonitor.DesktopContextMenuRequested += OnDesktopContextMenuRequested;
            _desktopInputMonitor.DesktopContextMenuCommandRequested += OnDesktopContextMenuCommandRequested;
            _desktopInputMonitor.DesktopContextMenuRefreshRequested += OnDesktopContextMenuRefreshRequested;
            _desktopInputMonitor.DesktopKeyboardCommandRequested += OnDesktopKeyboardCommandRequested;
        }
        _desktopInputMonitor.DesktopListView = _desktopHost.DesktopListView;
        _desktopInputMonitor.IsPointerOverBox = (x, y) =>
            _surfaceManager?.IsPointOverAnyBox(x, y) == true;
        _desktopInputMonitor.IsBoxItemDragActive = () =>
            !_disposed && !IsPaused && _surfaceManager?.IsBoxItemDragActive == true;
        _desktopInputMonitor.IsDesktopIconDragActive = () =>
            !_disposed && !IsPaused && _surfaceManager?.IsDesktopIconDragActive == true;
        _desktopInputMonitor.CanDeleteDesktopItems = () =>
            !_disposed && !IsPaused && _surfaceManager?.CanDeleteSelectedItems == true;
        _desktopInputMonitor.CanRenameDesktopItems = () =>
            !_disposed && !IsPaused && _surfaceManager?.CanRenameSelectedItem == true;
        // Answered on the input hook's thread while the key event waits, so
        // the manager's cheap interception check is used here. The precise
        // check runs in ExecuteDesktopKeyboardCommandAsync on the UI thread.
        _desktopInputMonitor.CanHandleDesktopKeyboardCommand = command =>
            !_disposed && !IsPaused && _surfaceManager?.CanInterceptDesktopKeyboardCommand(command) == true;
        _desktopInputMonitor.Enabled = true;
    }

    private void OnDesktopIconZoomRequested(object? sender, DesktopIconZoomEventArgs eventArgs)
    {
        // The low-level hook can run outside the UI thread; route the zoom
        // through the captured synchronization context like the other hook
        // originated handlers.
        BeginInvoke("desktop icon zoom", () =>
        {
            if (_disposed || IsPaused)
            {
                return;
            }

            // Ctrl+wheel over a box scales the icons of that box only. The
            // desktop path keeps feeding the native Explorer zoom synchronizer.
            if (_surfaceManager is not null &&
                _surfaceManager.TryZoomBoxIconsAt(eventArgs.X, eventArgs.Y, eventArgs.Delta))
            {
                return;
            }

            _desktopZoomTimer.Start();
        });
    }

    private void OnBoxDragMouseWheelRequested(object? sender, DesktopMouseWheelEventArgs eventArgs)
    {
        var shouldQueue = false;
        lock (_boxDragWheelGate)
        {
            _pendingBoxDragWheelDelta = Math.Clamp(
                _pendingBoxDragWheelDelta + eventArgs.Delta,
                -4800,
                4800);
            _pendingBoxDragWheelPoint = new Point(eventArgs.X, eventArgs.Y);
            if (!_boxDragWheelDispatchQueued)
            {
                _boxDragWheelDispatchQueued = true;
                shouldQueue = true;
            }
        }
        if (!shouldQueue)
        {
            return;
        }

        try
        {
            BeginInvoke("box drag wheel", DispatchBoxDragMouseWheel);
        }
        catch
        {
            lock (_boxDragWheelGate)
            {
                _boxDragWheelDispatchQueued = false;
            }
        }
    }

    private void DispatchBoxDragMouseWheel()
    {
        int delta;
        Point point;
        lock (_boxDragWheelGate)
        {
            delta = _pendingBoxDragWheelDelta;
            point = _pendingBoxDragWheelPoint;
            _pendingBoxDragWheelDelta = 0;
            _boxDragWheelDispatchQueued = false;
        }

        if (delta == 0 || _disposed || IsPaused ||
            (_surfaceManager?.IsBoxItemDragActive != true &&
             _surfaceManager?.IsDesktopIconDragActive != true))
        {
            return;
        }

        _surfaceManager.TryScrollBoxAt(point.X, point.Y, delta);
    }

    private void OnDesktopSurfaceClicked(object? sender, EventArgs eventArgs)
    {
        BeginInvoke("desktop surface click", () =>
        {
            // The low-level hook can observe the same button-down before the
            // icon surface's WinForms handler starts capture. Let that handler
            // own the gesture, including blank-area marquee selection.
            if (_surfaceManager?.IsDesktopIconPointerInteractionActive == true)
            {
                return;
            }

            _surfaceManager?.ClearSelection();
        });
    }

    private void OnDesktopKeyboardCommandRequested(
        object? sender,
        DesktopKeyboardCommandEventArgs eventArgs)
    {
        BeginInvoke(
            $"desktop keyboard command {eventArgs.Command}",
            () => _ = ExecuteDesktopKeyboardCommandAsync(eventArgs.Command));
    }

    private async Task ExecuteDesktopKeyboardCommandAsync(DesktopKeyboardCommand command)
    {
        try
        {
            if (_disposed || IsPaused || _surfaceManager is null)
            {
                return;
            }

            await _surfaceManager.ExecuteDesktopKeyboardCommandAsync(command);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error($"Desktop keyboard command '{command}' failed.", exception);
        }
    }

    private void OnDesktopContextMenuRequested(object? sender, EventArgs eventArgs)
    {
        // Explorer commits a native context-menu command only after the menu
        // closes. Poll briefly after the request so a chosen sort mode reaches
        // the replacement icon layer without waiting for the host health tick.
        BeginInvoke("desktop context menu opened", () =>
        {
            if (_disposed || IsPaused)
            {
                return;
            }

            _desktopViewRefreshDeadline = DateTimeOffset.UtcNow + DesktopViewRefreshWindow;
            _desktopViewRefreshTimer.Start();
        });
    }

    internal void NotifyDesktopContextMenuOpened()
    {
        // DesktopIconSurface forwards its background context menu by posting
        // WM_CONTEXTMENU to Explorer. Arm the input monitor explicitly so it
        // can identify a selected sort item even when SortColumns itself does
        // not change (for example, repeating the active sort mode).
        _desktopInputMonitor?.TrackDesktopContextMenu();
        OnDesktopContextMenuRequested(this, EventArgs.Empty);
    }

    private void OnDesktopContextMenuCommandRequested(object? sender, EventArgs eventArgs)
    {
        // The input hook sees the menu selection on mouse-down, before Explorer
        // necessarily publishes its new SortColumns value. Keep the saved grid
        // intact until a live, recognized sort state is available; an empty
        // transient snapshot must never turn this command into an A-Z rebuild.
        BeginInvoke("desktop menu command", () =>
        {
            if (_disposed || IsPaused)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            _desktopSortCommandPending = true;
            _desktopSortCommandReadyAt = now + DesktopSortCommandMinimumWait;
            _desktopViewRefreshDeadline = now + DesktopViewRefreshWindow;
            _desktopViewRefreshTimer.Start();
        });
    }

    private void OnDesktopContextMenuRefreshRequested(object? sender, EventArgs eventArgs)
    {
        DiagnosticLog.Info("Native desktop Refresh command detected by input monitor.");
        BeginInvoke("desktop menu refresh request", () =>
        {
            if (_disposed || IsPaused)
            {
                return;
            }

            RequestDesktopRefresh();
        });
    }

    /// <summary>
    /// Queues the refresh that both the desktop context menu's Refresh item and
    /// F5 ask for. Must be called on the UI thread.
    /// </summary>
    /// <remarks>
    /// A Refresh command leaves Explorer's sort, size, and visibility state
    /// unchanged, so the normal context-menu synchronization has no change token
    /// to act on. The short delay lets Explorer finish its own command first,
    /// and coalesces a burst — a held F5, a double-taken menu click — into one
    /// pass over the replacement icon layer. Public because the registered
    /// desktop menu reaches it through the application's command line.
    /// </remarks>
    public void RequestDesktopRefresh()
    {
        DiagnosticLog.Info("Desktop refresh queued.");
        _desktopMenuRefreshPending = true;
        _desktopViewRefreshDeadline = null;
        _desktopViewRefreshTimer.Stop();
        _desktopMenuRefreshTimer.Start();
    }

    private async void RefreshAfterDesktopMenuCommandAsync()
    {
        if (_disposed || IsPaused)
        {
            _desktopMenuRefreshPending = false;
            return;
        }
        if (_desktopMenuRefreshInProgress)
        {
            _desktopMenuRefreshTimer.Start();
            return;
        }
        if (!_desktopMenuRefreshPending)
        {
            return;
        }

        _desktopMenuRefreshPending = false;
        _desktopMenuRefreshInProgress = true;
        DiagnosticLog.Info("Desktop refresh execution started.");
        // Explorer owns the desktop view and its COM-backed state read can
        // block behind shell work. An explicit refresh must still re-enumerate
        // CrabDesk's items and redraw even if Explorer is busy. Use the last
        // known order when available; when the Shell exposes no sort at all,
        // make the explicit refresh useful by applying Created ascending.
        var hasAuthoritativeSort = _desktopSortStateIsAuthoritative;
        var sortState = ResolveDesktopRefreshSortState(_desktopSortState, hasAuthoritativeSort);
        var viewSignature = string.IsNullOrWhiteSpace(_desktopSortSignature)
            ? "unavailable"
            : _desktopSortSignature;
        if (!hasAuthoritativeSort)
        {
            _desktopSortState = sortState;
            if (TryStoreLastKnownDesktopSortState(State, sortState))
            {
                ScheduleSave();
            }
            DiagnosticLog.Info(
                $"Explorer desktop sort unavailable; applying and saving fallback " +
                $"mode={sortState.Mode} descending={sortState.Descending} for explicit refresh.");
        }

        // Dropping the persisted grid restarts the sequence from the first
        // cell, and the one-shot flag makes the icon surface apply this sort.
        _desktopRefreshResortPending = true;
        ResetDesktopIconLayoutForAutoArrange(refreshWorkspace: false);
        try
        {
            await RefreshItemsAsync(false);
            DiagnosticLog.Info(
                $"Explorer desktop refresh synchronized items={Items.Count} " +
                $"resorted mode={sortState.Mode} descending={sortState.Descending} " +
                $"sortSource={(hasAuthoritativeSort ? "last-known" : "Created fallback")} " +
                $"viewSignature='{viewSignature}'");
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Explorer desktop refresh synchronization failed", exception);
        }
        finally
        {
            _desktopRefreshResortPending = false;
            _desktopMenuRefreshInProgress = false;
            if (_desktopMenuRefreshPending && !_disposed && !IsPaused)
            {
                _desktopMenuRefreshTimer.Start();
            }
        }
    }

    private async void SynchronizeDesktopIconZoom()
    {
        if (_disposed)
        {
            return;
        }

        var shellState = await Task.Run(() =>
        {
            var desktopViewState = DesktopIconPositionService.GetDesktopViewState();
            var hasSpacing = DesktopIconPositionService.TryGetItemSpacing(
                _desktopHost.DesktopListView,
                out var spacing);
            return (DesktopViewState: desktopViewState, HasSpacing: hasSpacing, Spacing: spacing);
        });
        if (_disposed)
        {
            return;
        }
        var desktopViewState = shellState.DesktopViewState;
        if (desktopViewState.IconSize is not { } nativeIconSize)
        {
            return;
        }

        CaptureDesktopViewState(desktopViewState, out var sortChanged);
        DiagnosticLog.Info(
            $"Desktop icon zoom synchronized size={nativeIconSize} " +
            $"spacing={(shellState.HasSpacing ? $"{shellState.Spacing.Width}x{shellState.Spacing.Height}" : "unchanged")}");
        // Desktop icon zoom belongs to Explorer's unassigned-icon layer. Box
        // icon sizes remain an explicit per-box appearance setting.
        if (!TryApplyPendingDesktopSort(sortChanged, desktopViewState.HasLiveSortColumns) && !_desktopSortCommandPending)
        {
            RefreshDesktopView();
        }
    }

    private async void SynchronizeExplorerDesktopView()
    {
        if (_disposed || IsPaused)
        {
            _desktopViewRefreshDeadline = null;
            return;
        }

        try
        {
            var desktopViewState = await Task.Run(DesktopIconPositionService.GetDesktopViewState);
            if (_disposed || IsPaused)
            {
                _desktopViewRefreshDeadline = null;
                return;
            }
            var viewChanged = CaptureDesktopViewState(desktopViewState, out var sortChanged);
            if (TryApplyPendingDesktopSort(sortChanged, desktopViewState.HasLiveSortColumns))
            {
                return;
            }
            if (viewChanged)
            {
                // Unknown transient SortColumns can still change the view
                // signature. Redraw without dropping the saved grid, then keep
                // polling while the selected native sort is pending.
                RefreshDesktopView();
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Explorer desktop view synchronization failed", exception);
        }

        if (_desktopViewRefreshDeadline is { } deadline && DateTimeOffset.UtcNow < deadline)
        {
            _desktopViewRefreshTimer.Start();
        }
        else
        {
            _desktopViewRefreshDeadline = null;
            if (_desktopSortCommandPending)
            {
                _desktopSortCommandPending = false;
                _desktopSortCommandReadyAt = null;
                DiagnosticLog.Info(
                    "Explorer desktop sort command was not applied because no live sort state became available; " +
                    "the saved icon layout was preserved.");
            }
        }
    }

    /// <summary>
    /// Performs the one-time redraw for a native Sort by command. The flag
    /// remains set while <see cref="RefreshDesktopView"/> runs, which tells
    /// the desktop icon surface to ignore its persisted manual layout exactly
    /// once. This also covers selecting the currently active sort command,
    /// where Explorer leaves SortColumns unchanged.
    /// </summary>
    internal static DesktopIconSortState ResolveDesktopRefreshSortState(
        DesktopIconSortState currentSort,
        bool hasAuthoritativeSort) =>
        hasAuthoritativeSort
            ? currentSort
            : new DesktopIconSortState(DesktopIconSortMode.Created, Descending: false);

    internal static DesktopIconSortState? ResolveInitialDesktopSortState(
        DesktopIconViewState shellState,
        CrabDeskState state)
    {
        if (shellState.HasAuthoritativeSort)
        {
            return shellState.Sort;
        }

        return Enum.TryParse<DesktopIconSortMode>(
                state.LastKnownDesktopSortMode,
                ignoreCase: true,
                out var mode) &&
            Enum.IsDefined(mode)
                ? new DesktopIconSortState(mode, state.LastKnownDesktopSortDescending)
                : null;
    }

    internal static bool TryStoreLastKnownDesktopSortState(
        CrabDeskState state,
        DesktopIconSortState sort)
    {
        var mode = sort.Mode.ToString();
        if (string.Equals(state.LastKnownDesktopSortMode, mode, StringComparison.Ordinal) &&
            state.LastKnownDesktopSortDescending == sort.Descending)
        {
            return false;
        }

        state.LastKnownDesktopSortMode = mode;
        state.LastKnownDesktopSortDescending = sort.Descending;
        return true;
    }

    internal static bool ShouldApplyPendingDesktopSort(
        bool commandPending,
        bool hasLiveSortColumns,
        bool sortChanged,
        bool minimumWaitElapsed) =>
        commandPending && hasLiveSortColumns && (sortChanged || minimumWaitElapsed);

    private bool TryApplyPendingDesktopSort(bool sortChanged, bool hasLiveSortColumns)
    {
        var minimumWaitElapsed = _desktopSortCommandReadyAt is not { } readyAt ||
            DateTimeOffset.UtcNow >= readyAt;
        if (!ShouldApplyPendingDesktopSort(
                _desktopSortCommandPending,
                hasLiveSortColumns,
                sortChanged,
                minimumWaitElapsed))
        {
            return false;
        }

        // The click is observed before Explorer commits the menu command. Do
        // not discard manual cells until a recognized live sort is available.
        // Keep the pending flag true during this call so the surface bypasses
        // those cells for this one native sort operation.
        ResetDesktopIconLayoutForAutoArrange(refreshWorkspace: false);
        RefreshDesktopView();
        _desktopSortCommandPending = false;
        _desktopSortCommandReadyAt = null;
        _desktopViewRefreshDeadline = null;
        _desktopViewRefreshTimer.Stop();
        DiagnosticLog.Info("Explorer desktop sort command synchronized.");
        return true;
    }

    private bool CaptureDesktopViewState(
        DesktopIconViewState desktopViewState,
        out bool sortChanged)
    {
        sortChanged = desktopViewState.HasAuthoritativeSort &&
            _desktopSortStateIsAuthoritative &&
            _desktopSortState != desktopViewState.Sort;
        var autoArrangeChanged = _desktopAutoArrange != desktopViewState.AutoArrange;
        var changed = !string.Equals(
            _desktopSortSignature,
            desktopViewState.Signature,
            StringComparison.Ordinal);
        _desktopSortSignature = desktopViewState.Signature;
        var persistedSortChanged = desktopViewState.HasAuthoritativeSort &&
            TryStoreLastKnownDesktopSortState(State, desktopViewState.Sort);
        if (desktopViewState.HasAuthoritativeSort)
        {
            _desktopSortState = desktopViewState.Sort;
            _desktopSortStateIsAuthoritative = true;
        }
        _desktopAutoArrange = desktopViewState.AutoArrange;
        _desktopIconsVisible = desktopViewState.DesktopIconsVisible;
        var layoutCleared = false;
        if ((sortChanged || autoArrangeChanged) &&
            (State.DesktopIconPositions.Count > 0 || State.DesktopIconLayout.Count > 0))
        {
            State.DesktopIconPositions.Clear();
            State.DesktopIconLayout.Clear();
            layoutCleared = true;
            DiagnosticLog.Info(
                $"Desktop icon layout cleared after Explorer view change sortChanged={sortChanged} " +
                $"autoArrangeChanged={autoArrangeChanged} autoArrange={desktopViewState.AutoArrange}.");
        }
        if (persistedSortChanged || layoutCleared)
        {
            ScheduleSave();
        }
        if (changed)
        {
            // A pending sort still needs to reach TryApplyPendingDesktopSort
            // so the first replacement-layer redraw cannot restore the old
            // saved positions.
            if (!_desktopSortCommandPending)
            {
                _desktopViewRefreshDeadline = null;
                _desktopViewRefreshTimer.Stop();
            }
            DiagnosticLog.Info(
                $"Explorer desktop view changed mode={desktopViewState.Sort.Mode} " +
                $"descending={desktopViewState.Sort.Descending} " +
                $"iconSize={desktopViewState.IconSize?.ToString() ?? "unknown"} " +
                $"iconsVisible={desktopViewState.DesktopIconsVisible} " +
                $"sortColumns='{desktopViewState.Signature}'");
        }
        return changed;
    }

    private void RefreshDesktopView()
    {
        if (_disposed || IsPaused)
        {
            return;
        }

        _surfaceManager?.SetDesktopIconsVisible(_desktopIconsVisible);
        _surfaceManager?.Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnSystemPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs eventArgs)
    {
        if (State.Settings.ThemeMode != ApplicationThemeMode.System || _disposed)
        {
            return;
        }

        BeginInvoke("system theme changed", () => ApplyTheme(true));
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs eventArgs)
    {
        if (_disposed)
        {
            return;
        }
        if (eventArgs.Mode == Microsoft.Win32.PowerModes.Suspend)
        {
            BeginInvoke("power suspend", async () =>
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

        BeginInvoke("power resume", async () =>
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
        var preview = DesktopPreviewCapture.TryCapturePng(_desktopHost.DesktopParent);
        DiagnosticLog.Info(preview is null
            ? "Desktop backup preview capture was unavailable."
            : $"Desktop backup preview captured bytes={preview.Length}");
        return new DesktopBackupCapture(
            [],
            DesktopWallpaperService.GetCurrentWallpaperPath(),
            preview);
    }

    private void RestoreDesktopBackup(LayoutBackupSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.WallpaperPath))
        {
            DesktopWallpaperService.SetWallpaper(snapshot.WallpaperPath);
        }
    }

    private async Task ApplyLoadedStateAsync(CrabDeskState state)
    {
        RestoreAssignedItemVisibility(true);
        var localAiSettings = AiClassificationImportPolicy.PreserveLocalProfile(
            State.Settings.AiClassification,
            state.Settings.AiClassification);
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
        State.Settings.AiClassification = localAiSettings;
        SynchronizeBoxStyles();
        _lastOrganizationAssignments = null;
        _lastOrganizationCreatedBoxes.Clear();
        _workspaceRevision++;
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
        var diagnosticStarted = Stopwatch.StartNew();
        try
        {
            await SaveNowAsync();
            DiagnosticLog.Info($"Autosave timing elapsedMs={diagnosticStarted.ElapsedMilliseconds}");
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
        // Every metric below is authored in DIPs. WinForms renders the
        // point-sized menu font at the monitor DPI, so the fixed row height,
        // paddings and minimum width must scale together with it - otherwise
        // the text overflows the rows and gets clipped on high-DPI displays.
        var dpiScale = GetMenuDpiScale(menu);
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = false;
        var minimumWidth = menu is FluentContextMenuStrip fluentMenu
            ? fluentMenu.MinimumMenuWidth
            : 112;
        minimumWidth = (int)Math.Round(minimumWidth * dpiScale);
        var menuWidth = CalculateMenuWidth(
            menu.Items,
            minimumWidth,
            dpiScale,
            menu.Padding.Horizontal);
        menu.MinimumSize = new System.Drawing.Size(menuWidth, 0);
        menu.DropShadowEnabled = true;
        ApplyTrayColors(menu.Items, IsDarkTheme);
        menu.BackColor = IsDarkTheme
            ? System.Drawing.Color.FromArgb(37, 40, 45)
            : System.Drawing.Color.FromArgb(252, 252, 252);
        menu.ForeColor = IsDarkTheme
            ? System.Drawing.Color.FromArgb(244, 245, 247)
            : System.Drawing.Color.FromArgb(32, 36, 42);
        ApplyMenuMetrics(menu.Items, menuWidth - menu.Padding.Horizontal, dpiScale);
        menu.PerformLayout();
    }

    /// <summary>
    /// Returns the menu levels whose metrics should be configured for the
    /// current presentation pass. Root-menu configuration deliberately does
    /// not traverse closed submenus: opening a submenu configures that level
    /// on demand, so constructing a root menu does not eagerly create every
    /// nested native popup.
    /// </summary>
    internal static IReadOnlyList<System.Windows.Forms.ToolStripDropDown> GetMenuLevelsToConfigure(
        System.Windows.Forms.ContextMenuStrip root,
        bool includeClosedSubmenus)
    {
        ArgumentNullException.ThrowIfNull(root);

        var levels = new List<System.Windows.Forms.ToolStripDropDown> { root };
        if (!includeClosedSubmenus)
        {
            return levels;
        }

        AddSubmenuLevels(root.Items, levels);
        return levels;
    }

    private static void AddSubmenuLevels(
        System.Windows.Forms.ToolStripItemCollection items,
        List<System.Windows.Forms.ToolStripDropDown> levels)
    {
        foreach (var item in items.OfType<System.Windows.Forms.ToolStripMenuItem>())
        {
            if (!item.HasDropDownItems)
            {
                continue;
            }

            levels.Add(item.DropDown);
            AddSubmenuLevels(item.DropDownItems, levels);
        }
    }

    internal static float GetMenuDpiScale(System.Windows.Forms.ContextMenuStrip menu)
    {
        var preferredDpiScale = menu is FluentContextMenuStrip fluentMenu
            ? fluentMenu.PreferredDpiScale
            : null;
        try
        {
            return FluentContextMenuStrip.ResolveMetricsDpiScale(
                menu.IsHandleCreated,
                menu.DeviceDpi,
                preferredDpiScale);
        }
        catch
        {
            return preferredDpiScale is > 0
                ? Math.Max(0.75f, preferredDpiScale.Value)
                : 1f;
        }
    }

    private int CalculateMenuWidth(
        System.Windows.Forms.ToolStripItemCollection items,
        int minimumWidth,
        float dpiScale,
        int horizontalMenuPadding)
    {
        var widestText = 0;
        var hasSubmenu = false;
        foreach (System.Windows.Forms.ToolStripItem item in items)
        {
            if (item is System.Windows.Forms.ToolStripSeparator)
            {
                continue;
            }

            widestText = Math.Max(
                widestText,
                System.Windows.Forms.TextRenderer.MeasureText(
                    item.Text,
                    item.Font ?? _menuFont,
                    System.Drawing.Size.Empty,
                    System.Windows.Forms.TextFormatFlags.SingleLine |
                    System.Windows.Forms.TextFormatFlags.NoPadding).Width);
            hasSubmenu |= item is System.Windows.Forms.ToolStripMenuItem { HasDropDownItems: true };
        }

        var leadingSlot = (int)Math.Ceiling(32 * dpiScale);
        var trailingSlot = (int)Math.Ceiling((hasSubmenu ? 30 : 10) * dpiScale);
        var itemMargins = (int)Math.Ceiling(2 * dpiScale);
        return Math.Max(
            minimumWidth,
            leadingSlot + widestText + trailingSlot + horizontalMenuPadding + itemMargins);
    }

    private void ApplyMenuMetrics(
        System.Windows.Forms.ToolStripItemCollection items,
        int availableWidth,
        float dpiScale)
    {
        var itemMargin = (int)Math.Round(1 * dpiScale);
        var itemHeight = (int)Math.Round(30 * dpiScale);
        var separatorMarginX = (int)Math.Round(8 * dpiScale);
        var separatorMarginY = (int)Math.Round(3 * dpiScale);
        foreach (System.Windows.Forms.ToolStripItem item in items)
        {
            if (item is System.Windows.Forms.ToolStripSeparator)
            {
                item.Margin = new System.Windows.Forms.Padding(
                    separatorMarginX,
                    separatorMarginY,
                    separatorMarginX,
                    separatorMarginY);
                item.AutoSize = false;
                item.Size = new System.Drawing.Size(
                    Math.Max(1, availableWidth - item.Margin.Horizontal),
                    Math.Max(1, (int)Math.Round(dpiScale)));
                continue;
            }
            item.Padding = System.Windows.Forms.Padding.Empty;
            item.Margin = new System.Windows.Forms.Padding(itemMargin, 0, itemMargin, 0);
            item.AutoSize = false;
            item.Size = new System.Drawing.Size(
                Math.Max(1, availableWidth - item.Margin.Horizontal),
                itemHeight);
            if (item is System.Windows.Forms.ToolStripMenuItem menuItem)
            {
                var dropDown = menuItem.DropDown;
                dropDown.Renderer = IsDarkTheme ? _darkTrayRenderer : _lightTrayRenderer;
                dropDown.Font = _menuFont;
                if (dropDown is System.Windows.Forms.ToolStripDropDownMenu dropDownMenu)
                {
                    dropDownMenu.ShowImageMargin = false;
                    dropDownMenu.ShowCheckMargin = false;
                }
                dropDown.BackColor = IsDarkTheme
                    ? System.Drawing.Color.FromArgb(37, 40, 45)
                    : System.Drawing.Color.FromArgb(252, 252, 252);
                dropDown.ForeColor = IsDarkTheme
                    ? System.Drawing.Color.FromArgb(244, 245, 247)
                    : System.Drawing.Color.FromArgb(32, 36, 42);
                var dropDownWidth = CalculateMenuWidth(
                    menuItem.DropDownItems,
                    (int)Math.Round(112 * dpiScale),
                    dpiScale,
                    dropDown.Padding.Horizontal);
                dropDown.MinimumSize = new System.Drawing.Size(dropDownWidth, 0);
                ApplyMenuMetrics(
                    menuItem.DropDownItems,
                    dropDownWidth - dropDown.Padding.Horizontal,
                    dpiScale);
                dropDown.PerformLayout();
                _ = _configuredSubmenus.GetValue(dropDown, candidate =>
                {
                    candidate.Opened += OnSubmenuOpened;
                    return new object();
                });
                StretchDropDownItems(dropDown);
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
        dropDown.Update();
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
                item.Bounds.Left -
                item.Margin.Right);
            item.AutoSize = false;
            item.Width = width;
        }
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
        var item = _allDesktopItems.FirstOrDefault(candidate => candidate.Key.ToString() == itemKey);
        State.Assignments.Remove(itemKey);
        MoveItemOrderKey(itemKey, null);
        if (item?.FileSystemPath is { } pathValue && !string.IsNullOrWhiteSpace(pathValue))
        {
            var path = Path.GetFullPath(pathValue);
            if (_originalFileAttributes.Remove(path, out var originalAttributes))
            {
                try
                {
                    if ((File.Exists(path) || Directory.Exists(path)) &&
                        File.GetAttributes(path) != originalAttributes)
                    {
                        File.SetAttributes(path, originalAttributes);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        else if (item?.Kind == DesktopItemKind.Shell &&
                 item.ParsingName is { } parsingName &&
                 TryGetShellClsid(parsingName, out var clsid) &&
                 _hiddenShellIconOriginals.Remove(clsid, out var previousVisibility))
        {
            try
            {
                WriteShellIconVisibility(clsid, previousVisibility);
            }
            catch (Exception exception)
            {
                DiagnosticLog.Error($"Failed to restore shell desktop icon clsid={clsid}", exception);
            }
        }
    }

    private async void OnDesktopItemsChanged(object? sender, EventArgs eventArgs)
    {
        if (_disposed)
        {
            return;
        }
        // New and deleted items must always appear or disappear on the
        // replacement desktop, even when the 'refresh after rename'
        // preference is off. Only a rename honors that gate so a manual
        // layout stays stable while the user is typing a new name.
        var isRename = eventArgs is FileSystemEventArgs fileArgs &&
            fileArgs.ChangeType == WatcherChangeTypes.Renamed;
        DiagnosticLog.Info(
            $"Desktop change type={(eventArgs as FileSystemEventArgs)?.ChangeType}" +
            $"{(eventArgs is FileSystemEventArgs fe ? $" path={fe.FullPath}" : "")}");
        var realtimeOrganization = State.Organization.Enabled && State.Organization.RunOnDesktopChanges;
        if (isRename && !State.Settings.DesktopBehavior.RefreshAfterRename && !realtimeOrganization)
        {
            return;
        }
        try
        {
            // WatcherChangeTypes.All is the watcher reporting that it lost
            // notifications (buffer overflow): only a full pass is safe then.
            if (!realtimeOrganization &&
                eventArgs is FileSystemEventArgs { ChangeType: not WatcherChangeTypes.All } ownedArgs &&
                ShouldSuppressTargetedDesktopRefresh(ownedArgs.FullPath))
            {
                await RefreshDesktopItemsAfterOwnedChangeAsync();
                return;
            }

            await RefreshItemsAsync();
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Desktop item refresh failed", exception);
        }
    }

    // Reconciles the snapshot after a change CrabDesk performed itself. The
    // operation already repainted its own region, so running the full surface
    // pass here would rebuild every monitor and drop every icon cache a quarter
    // second after the paste finished. The snapshot is still refreshed, so a
    // change that slipped in alongside the owned one is not lost: only the
    // desktop cells and boxes that actually changed are repainted, and anything
    // this diff cannot express falls back to the full pass.
    private async Task RefreshDesktopItemsAfterOwnedChangeAsync()
    {
        var previous = GetDesktopItemIdentities();
        var previousAssignments = new Dictionary<string, Guid>(State.Assignments, StringComparer.OrdinalIgnoreCase);
        await RefreshItemsCoreAsync(refreshSurfaces: false, applyDesktopRules: false);
        var current = GetDesktopItemIdentities();

        // A stable key survives a rename, so an item that kept its key while
        // its name or path changed is neither an addition nor a removal and
        // cannot be expressed as a set of dirty cells.
        if (current.Any(entry =>
                previous.TryGetValue(entry.Key, out var previousIdentity) &&
                !string.Equals(previousIdentity, entry.Value, StringComparison.Ordinal)))
        {
            _surfaceManager?.Refresh();
            return;
        }

        var addedKeys = current.Keys.Where(key => !previous.ContainsKey(key)).ToArray();
        var removedKeys = previous.Keys.Where(key => !current.ContainsKey(key)).ToArray();
        if (addedKeys.Length == 0 && removedKeys.Length == 0)
        {
            DiagnosticLog.Info("Desktop change already reflected by the operation that caused it");
            return;
        }

        // An item that belongs to a box changes that box, not a desktop cell.
        var changedBoxIds = addedKeys
            .Select(key => State.Assignments.TryGetValue(key, out var boxId) ? (Guid?)boxId : null)
            .Concat(removedKeys.Select(key =>
                previousAssignments.TryGetValue(key, out var boxId) ? (Guid?)boxId : null))
            .OfType<Guid>()
            .ToHashSet();
        var addedDesktopKeys = addedKeys
            .Where(key => !State.Assignments.ContainsKey(key))
            .ToArray();
        var removedDesktopKeys = removedKeys
            .Where(key => !previousAssignments.ContainsKey(key))
            .ToArray();

        DiagnosticLog.Info(
            "Desktop change reconciled without a full refresh " +
            $"added={addedDesktopKeys.Length} removed={removedDesktopKeys.Length} boxes={changedBoxIds.Count}");
        if (changedBoxIds.Count > 0)
        {
            _surfaceManager?.RefreshBoxItems(changedBoxIds);
        }
        if (removedDesktopKeys.Length > 0)
        {
            _surfaceManager?.RefreshDesktopItemsRemoved(removedDesktopKeys);
        }
        if (addedDesktopKeys.Length > 0)
        {
            _surfaceManager?.RefreshDesktopItemsAdded(addedDesktopKeys);
        }
    }

    // "|" cannot appear in a Windows file name, so it safely separates the two
    // parts of the rename-detection identity.
    private Dictionary<string, string> GetDesktopItemIdentities() => Items
        .GroupBy(item => item.Key.ToString(), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            group => group.Key,
            group => $"{group.First().DisplayName}|{group.First().FileSystemPath}",
            StringComparer.OrdinalIgnoreCase);

    private void MoveItemOrderKey(string itemKey, Guid? targetBoxId, string? beforeKey = null, Guid? targetTabId = null)
    {
        foreach (var box in State.Boxes)
        {
            box.ItemOrder.RemoveAll(key => string.Equals(key, itemKey, StringComparison.OrdinalIgnoreCase));
            if (targetBoxId != box.Id)
            {
                box.ItemTabAssignments.Remove(itemKey);
            }
        }
        if (targetBoxId is { } target)
        {
            var targetBox = State.Boxes.FirstOrDefault(box => box.Id == target);
            if (targetBox is not null)
            {
                if (targetTabId is not null)
                {
                    targetBox.ItemTabAssignments[itemKey] = targetTabId.Value;
                }
                if (!string.IsNullOrWhiteSpace(beforeKey))
                {
                    var index = targetBox.ItemOrder.FindIndex(k => string.Equals(k, beforeKey, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0)
                    {
                        targetBox.ItemOrder.Insert(index, itemKey);
                        return;
                    }
                }
                targetBox.ItemOrder.Add(itemKey);
            }
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
            if (box.ItemTabAssignments.Remove(oldKey, out var tabId))
            {
                box.ItemTabAssignments[newKey] = tabId;
            }
        }
    }

    private static string GetUniqueManualTabTitle(DesktopBox box, string title, Guid? excludingTabId = null)
    {
        var baseTitle = string.IsNullOrWhiteSpace(title) ? "新标签" : title.Trim();
        var existingTitles = box.ManualTabs
            .Where(tab => tab.Id != excludingTabId)
            .Select(tab => tab.Title)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!existingTitles.Contains(baseTitle))
        {
            return baseTitle;
        }

        var index = 2;
        string candidate;
        do
        {
            candidate = $"{baseTitle} ({index++})";
        }
        while (existingTitles.Contains(candidate));
        return candidate;
    }

    private static string FormatHandle(IntPtr handle) => $"0x{handle.ToInt64():X}";

    private sealed class AiClassificationUsageAccumulator(
        IProgress<AiClassificationUsageProgress>? progress) : IProgress<AiClassificationRequestUsage>
    {
        private bool _hasInputTokens = true;
        private bool _hasOutputTokens = true;
        private bool _hasTotalTokens = true;
        private int _inputTokens;
        private int _outputTokens;
        private int _totalTokens;
        private TimeSpan? _firstTokenLatency;
        private int _completedRequests;

        public void Report(AiClassificationRequestUsage value)
        {
            _completedRequests++;
            _firstTokenLatency ??= value.FirstTokenLatency;
            Add(value.InputTokens, ref _hasInputTokens, ref _inputTokens);
            Add(value.OutputTokens, ref _hasOutputTokens, ref _outputTokens);
            Add(value.TotalTokens, ref _hasTotalTokens, ref _totalTokens);
            progress?.Report(new AiClassificationUsageProgress(
                _firstTokenLatency,
                _hasInputTokens ? _inputTokens : null,
                _hasOutputTokens ? _outputTokens : null,
                _hasTotalTokens ? _totalTokens : null,
                _completedRequests));
        }

        private static void Add(int? value, ref bool hasValuesForAllRequests, ref int total)
        {
            hasValuesForAllRequests &= value.HasValue;
            if (value is { } count)
            {
                total = checked(total + count);
            }
        }
    }

    private sealed class DirectProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

}
