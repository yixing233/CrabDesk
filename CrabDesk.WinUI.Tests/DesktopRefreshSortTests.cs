using CrabDesk.Core;
using CrabDesk.Native;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

/// <summary>
/// Pins that an explicit desktop Refresh — the desktop menu's Refresh item or F5
/// — re-lays the icons out in the last known sort order, falling back to Created
/// ascending when Explorer exposes no authoritative sort snapshot. The trigger
/// is a Shell menu command and the effect is a grid rebuild, so these are
/// asserted on the source.
/// </summary>
public sealed class DesktopRefreshSortTests
{
    [Fact]
    public void ExplicitRefreshAlwaysDropsTheManualGridAndChoosesAnAvailableSort()
    {
        var method = ReadRefreshMethod();

        var sort = method.IndexOf("ResolveDesktopRefreshSortState", StringComparison.Ordinal);
        var flag = method.IndexOf("_desktopRefreshResortPending = true;", StringComparison.Ordinal);
        var reset = method.IndexOf(
            "ResetDesktopIconLayoutForAutoArrange(refreshWorkspace: false);",
            StringComparison.Ordinal);
        var refresh = method.IndexOf("await RefreshItemsAsync(false);", StringComparison.Ordinal);

        Assert.True(sort >= 0, "Refresh must resolve a sort even if Explorer has no authoritative snapshot.");
        Assert.True(flag > sort);
        Assert.True(reset > flag, "The saved grid must be dropped before the rebuild reads it.");
        Assert.True(refresh > reset, "Clearing the layout after the rebuild would come one pass too late.");
        Assert.Contains("Created fallback", method, StringComparison.Ordinal);

        // refreshWorkspace: true would refresh the surfaces a second time, once
        // with the layout already gone and once through RefreshItemsAsync.
        Assert.DoesNotContain(
            "ResetDesktopIconLayoutForAutoArrange();",
            method,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnExplicitRefreshUsesCreatedAscendingWhenExplorerSortIsUnavailable()
    {
        var sort = CrabDeskRuntime.ResolveDesktopRefreshSortState(
            new DesktopIconSortState(DesktopIconSortMode.Name, Descending: false),
            hasAuthoritativeSort: false);

        Assert.Equal(new DesktopIconSortState(DesktopIconSortMode.Created, Descending: false), sort);
    }

    [Fact]
    public void GeometryKeepsTheRuntimeSortUntilExplorerReportsAnAuthoritativeSort()
    {
        var runtimeSort = new DesktopIconSortState(DesktopIconSortMode.Created, Descending: false);
        var unknownShellState = new DesktopIconViewState(
            new DesktopIconSortState(DesktopIconSortMode.Name, Descending: false),
            48,
            true,
            false,
            "shell:|size:57|flags:48200224",
            HasAuthoritativeSort: false,
            HasLiveSortColumns: false);
        var liveShellState = unknownShellState with
        {
            Sort = new DesktopIconSortState(DesktopIconSortMode.Modified, Descending: true),
            HasAuthoritativeSort = true,
            HasLiveSortColumns = true
        };

        // Clearing the one-shot resort flag must not let an unknown/stale Name
        // snapshot undo the Created sort on the very next ordinary rebuild.
        Assert.Equal(
            runtimeSort,
            DesktopIconSurface.ResolveGeometrySortState(
                resortPending: false,
                runtimeSort,
                unknownShellState));
        Assert.Equal(
            runtimeSort,
            DesktopIconSurface.ResolveGeometrySortState(
                resortPending: true,
                runtimeSort,
                liveShellState));
        Assert.Equal(
            liveShellState.Sort,
            DesktopIconSurface.ResolveGeometrySortState(
                resortPending: false,
                runtimeSort,
                liveShellState));

        var rebuild = ExtractMethod(
            ReadRuntimeSource("DesktopIconSurface.cs"),
            "internal static DesktopIconSortState ResolveGeometrySortState(",
            "private void RebuildGeometry()");
        Assert.Contains("resortPending || !desktopViewState.HasAuthoritativeSort", rebuild, StringComparison.Ordinal);
        Assert.Contains("? runtimeSort", rebuild, StringComparison.Ordinal);
        Assert.Contains(": desktopViewState.Sort;", rebuild, StringComparison.Ordinal);

        var geometry = ExtractMethod(
            ReadRuntimeSource("DesktopIconSurface.cs"),
            "private void RebuildGeometry()",
            "private DesktopIconLayoutSnapshot? GetStoredLayoutPlacement(");
        Assert.Contains("ResolveGeometrySortState(", geometry, StringComparison.Ordinal);
        Assert.Contains("OrderDesktopItems(", geometry, StringComparison.Ordinal);
        Assert.Contains("desktopSortState))", geometry, StringComparison.Ordinal);
        Assert.DoesNotContain("desktopViewState.Sort))", geometry, StringComparison.Ordinal);
    }

    [Fact]
    public void AColdStartRefreshCommandIsDispatchedAfterRuntimeInitialization()
    {
        var app = File.ReadAllText(Path.Combine(
            FindSolutionDirectory(),
            "CrabDesk.WinUI",
            "App.xaml.cs"));
        var listenersReady = app.IndexOf("StartCommandListeners(runtime, _window.DispatcherQueue);", StringComparison.Ordinal);
        var refreshDispatch = app.IndexOf("if (refreshDesktop) runtime.RequestDesktopRefresh();", StringComparison.Ordinal);

        Assert.True(listenersReady >= 0);
        Assert.True(refreshDispatch > listenersReady, "A first-instance command must run after the refresh timer and runtime are initialized.");
        Assert.Contains("var background = refreshDesktop ||", app, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupRestoresThePersistedSortWhenExplorerHasNoAuthoritativeSnapshot()
    {
        var state = JsonLayoutStore.CreateDefaultState();
        state.LastKnownDesktopSortMode = nameof(DesktopIconSortMode.Created);
        state.LastKnownDesktopSortDescending = true;
        var unknownShellState = new DesktopIconViewState(
            new DesktopIconSortState(DesktopIconSortMode.Name, false),
            48,
            true,
            false,
            "shell:|size:57|flags:48200224",
            HasAuthoritativeSort: false,
            HasLiveSortColumns: false);

        var restored = CrabDeskRuntime.ResolveInitialDesktopSortState(unknownShellState, state);

        Assert.Equal(new DesktopIconSortState(DesktopIconSortMode.Created, true), restored);
    }

    [Fact]
    public void LiveExplorerSortWinsOverThePreviouslyPersistedSort()
    {
        var state = JsonLayoutStore.CreateDefaultState();
        state.LastKnownDesktopSortMode = nameof(DesktopIconSortMode.Created);
        state.LastKnownDesktopSortDescending = true;
        var liveShellState = new DesktopIconViewState(
            new DesktopIconSortState(DesktopIconSortMode.Modified, false),
            48,
            true,
            false,
            "shell:prop:System.DateModified;",
            HasAuthoritativeSort: true,
            HasLiveSortColumns: true);

        var resolved = CrabDeskRuntime.ResolveInitialDesktopSortState(liveShellState, state);

        Assert.Equal(new DesktopIconSortState(DesktopIconSortMode.Modified, false), resolved);
    }

    [Fact]
    public void InvalidPersistedSortDoesNotBecomeAnAuthoritativeSort()
    {
        var state = JsonLayoutStore.CreateDefaultState();
        state.LastKnownDesktopSortMode = "NotARealSort";
        var unknownShellState = new DesktopIconViewState(
            new DesktopIconSortState(DesktopIconSortMode.Name, false),
            48,
            true,
            false,
            "shell:|size:57|flags:48200224",
            HasAuthoritativeSort: false,
            HasLiveSortColumns: false);

        var resolved = CrabDeskRuntime.ResolveInitialDesktopSortState(unknownShellState, state);

        Assert.Null(resolved);
    }

    [Fact]
    public void NewlyObservedAuthoritativeSortIsPersistedForTheNextLaunch()
    {
        var state = JsonLayoutStore.CreateDefaultState();
        var sort = new DesktopIconSortState(DesktopIconSortMode.Created, Descending: false);

        var firstUpdate = CrabDeskRuntime.TryStoreLastKnownDesktopSortState(state, sort);
        var repeatedUpdate = CrabDeskRuntime.TryStoreLastKnownDesktopSortState(state, sort);

        Assert.True(firstUpdate);
        Assert.Equal(nameof(DesktopIconSortMode.Created), state.LastKnownDesktopSortMode);
        Assert.False(state.LastKnownDesktopSortDescending);
        Assert.False(repeatedUpdate);

        var capture = ExtractMethod(
            ReadRuntimeSource("CrabDeskRuntime.cs"),
            "private bool CaptureDesktopViewState(",
            "private void RefreshDesktopView()");
        Assert.Contains("desktopViewState.HasAuthoritativeSort &&", capture, StringComparison.Ordinal);
        Assert.Contains("TryStoreLastKnownDesktopSortState(State, desktopViewState.Sort)", capture, StringComparison.Ordinal);
        Assert.Contains("if (persistedSortChanged || layoutCleared)", capture, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefreshDoesNotWaitForExplorersComViewReadBeforeRefreshingItems()
    {
        var method = ReadRefreshMethod();

        var liveRead = method.IndexOf(
            "DesktopIconPositionService.GetDesktopViewState",
            StringComparison.Ordinal);
        var resortDecision = method.IndexOf(
            "ResolveDesktopRefreshSortState(_desktopSortState, hasAuthoritativeSort)",
            StringComparison.Ordinal);
        var flag = method.IndexOf("_desktopRefreshResortPending = true;", StringComparison.Ordinal);
        var refresh = method.IndexOf("await RefreshItemsAsync(false);", StringComparison.Ordinal);

        Assert.True(liveRead < 0, "A Shell COM read must not block an explicit refresh.");
        Assert.True(resortDecision >= 0, "Use the last-known sort or the deterministic Created fallback.");
        Assert.True(flag > resortDecision, "Resolve the sort before the rebuild is marked for resort.");
        Assert.True(refresh > flag, "The item and surface refresh must follow the resort decision.");
    }

    [Fact]
    public void ATransientUnknownSortSnapshotCannotConsumeOrClearAPendingSort()
    {
        var runtime = ReadRuntimeSource("CrabDeskRuntime.cs");
        var command = ExtractMethod(
            runtime,
            "private void OnDesktopContextMenuCommandRequested(",
            "private void OnDesktopContextMenuRefreshRequested(");
        var apply = ExtractMethod(
            runtime,
            "private bool TryApplyPendingDesktopSort(",
            "private bool CaptureDesktopViewState(");

        Assert.DoesNotContain("ResetDesktopIconLayoutForAutoArrange", command, StringComparison.Ordinal);
        Assert.Contains("hasLiveSortColumns", apply, StringComparison.Ordinal);
        Assert.Contains("desktopViewState.HasLiveSortColumns", runtime, StringComparison.Ordinal);
        Assert.Contains("sortChanged", apply, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false, true, true, false)]
    [InlineData(true, true, true, false, true)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, true, false, true, true)]
    [InlineData(false, true, true, true, false)]
    public void PendingSortRequiresALiveSortAndEitherAChangeOrReadyDelay(
        bool pending,
        bool hasLiveSort,
        bool sortChanged,
        bool minimumWaitElapsed,
        bool expected)
    {
        Assert.Equal(
            expected,
            CrabDeskRuntime.ShouldApplyPendingDesktopSort(
                pending,
                hasLiveSort,
                sortChanged,
                minimumWaitElapsed));
    }

    [Fact]
    public void TheResortFlagIsDroppedAgainEvenWhenTheRefreshFails()
    {
        var method = ReadRefreshMethod();

        // Leaving it set would make the next unrelated rebuild — a new file, a
        // monitor change — throw away the arrangement as well.
        var finallyBlock = method.IndexOf("finally", StringComparison.Ordinal);
        var cleared = method.IndexOf("_desktopRefreshResortPending = false;", StringComparison.Ordinal);
        Assert.True(finallyBlock >= 0);
        Assert.True(cleared > finallyBlock, "The flag must be cleared from the finally block.");
    }

    [Fact]
    public void OnlyAnExplicitRefreshOrANativeSortCommandEverReSortsTheDesktop()
    {
        var source = ReadRuntimeSource("CrabDeskRuntime.cs");

        // A file appearing, a rename, a monitor change and a paste all end in a
        // refresh too. If any of them opted into the flag, it would silently
        // undo the positions the user dragged their icons to.
        Assert.Equal(1, CountOccurrences(source, "_desktopRefreshResortPending = true;"));
        Assert.Equal(1, CountOccurrences(source, "_desktopSortCommandPending = true;"));

        // Two independent one-shots: a Sort by command that lands while a
        // refresh is in flight must not have its redraw consumed by the
        // refresh's finally block, and vice versa.
        var property = ExtractMethod(
            source,
            "internal bool IsDesktopResortPending =>",
            "internal bool TryDropDesktopItemsIntoBox(");
        Assert.Contains(
            "_desktopSortCommandPending || _desktopRefreshResortPending;",
            property,
            StringComparison.Ordinal);
    }

    [Fact]
    public void APendingResortRebuildsFromTheSortOrderInsteadOfTheSavedCells()
    {
        var rebuild = ExtractMethod(
            ReadRuntimeSource("DesktopIconSurface.cs"),
            "private void RebuildGeometry()",
            "private DesktopIconLayoutSnapshot? GetStoredLayoutPlacement(");

        Assert.Contains("!_runtime.IsDesktopResortPending &&", rebuild, StringComparison.Ordinal);

        // With no stored cells left, every item takes the auto-placement path,
        // which is the one that honours the active sort property.
        var storedLayout = rebuild.IndexOf("var storedLayout = useStoredLayout", StringComparison.Ordinal);
        var ordering = rebuild.IndexOf("desktopSortState))", StringComparison.Ordinal);
        Assert.True(storedLayout >= 0);
        Assert.True(ordering > storedLayout);
        Assert.Contains("var cell = FindFirstFreeCell(grid, occupiedCells);", rebuild, StringComparison.Ordinal);

        // The sorted round must not be written back as the manual layout while
        // it is still being applied; the next ordinary rebuild persists it.
        var persist = rebuild.IndexOf("if (useStoredLayout)", StringComparison.Ordinal);
        Assert.True(persist > ordering);
        Assert.Contains(
            "PersistCurrentLayoutIfNeeded(storedLayout);",
            rebuild[persist..],
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnExplorerReconnectKeepsTheSavedGridWhenAutoArrangeIsTransientlyReported()
    {
        var rebuild = ExtractMethod(
            ReadRuntimeSource("DesktopIconSurface.cs"),
            "private void RebuildGeometry()",
            "private DesktopIconLayoutSnapshot? GetStoredLayoutPlacement(");

        Assert.Contains("var hasStoredLayout = _runtime.State.DesktopIconLayout.Count > 0;", rebuild, StringComparison.Ordinal);
        Assert.Contains("(!desktopViewState.AutoArrange || hasStoredLayout);", rebuild, StringComparison.Ordinal);
        Assert.Contains(
            "Explorer can briefly report its default AutoArrange flag",
            rebuild,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SmallExplorerSpacingCannotCollapseTheReplacementGrid()
    {
        var source = ReadRuntimeSource("DesktopIconSurface.cs");

        Assert.Contains("Math.Max(nativeSpacing.Width / scale, DefaultHorizontalSpacing)", source, StringComparison.Ordinal);
        Assert.Contains("Math.Max(nativeSpacing.Height / scale, DefaultVerticalSpacing)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRegisteredDesktopMenuOffersRefreshViaTheCommandLine()
    {
        // The desktop context menu CrabDesk shows is registered under
        // DesktopBackground\Shell and reached as a command line, not through the
        // in-process WinForms background menu. Registering the Refresh verb here
        // is what makes the entry appear and run the shared refresh pipeline.
        var registration = ReadNativeSource("DesktopContextMenuRegistration.cs");
        Assert.Contains("\"06Refresh\"", registration, StringComparison.Ordinal);
        Assert.Contains("\"--refresh-desktop\"", registration, StringComparison.Ordinal);

        var app = File.ReadAllText(Path.Combine(
            FindSolutionDirectory(),
            "CrabDesk.WinUI",
            "App.xaml.cs"));
        Assert.Contains("\"--refresh-desktop\"", app, StringComparison.Ordinal);
        Assert.Contains(@"Local\CrabDesk.RefreshDesktop", app, StringComparison.Ordinal);
        Assert.Contains("runtime.RequestDesktopRefresh", app, StringComparison.Ordinal);
    }

    [Fact]
    public void ForwardingTheContextMenuNeverRevealsExplorersNativeIconLayer()
    {
        var source = ReadNativeSource("DesktopWindowTools.cs");
        var method = ExtractMethod(
            source,
            "public static bool ShowDesktopContextMenu",
            "/// <summary>");

        // Showing the native ListView so Explorer would build its popup painted
        // the real desktop icons on top of the replacement layer for as long as
        // the menu was open. The forwarding path must stay invisible: it only
        // posts the message and never changes the ListView's visibility.
        Assert.DoesNotContain("EnsureDesktopIconViewVisible", method, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowWindow", method, StringComparison.Ordinal);
        Assert.Contains("NativeMethods.PostMessage", method, StringComparison.Ordinal);
    }

    [Fact]
    public void NativePopupClassificationStaysCheapOnTheInputHookThread()
    {
        // The WH_MOUSE_LL callback blocks all system mouse input while it runs.
        // Widening the native-menu lookup to several candidate windows meant up
        // to a dozen cross-process SendMessageTimeout calls per click, which
        // stalled the very click that should dismiss an open menu.
        var source = ReadNativeSource("DesktopDoubleClickMonitor.cs");
        var classify = ExtractMethod(
            source,
            "private static bool IsNativeRefreshMenuItem(",
            "private static bool IsNativeMenuWindow(");

        Assert.Contains("GetAncestor(targetWindow, 2)", classify, StringComparison.Ordinal);
        Assert.DoesNotContain("GetForegroundWindow()", classify, StringComparison.Ordinal);
        Assert.DoesNotContain("GetMenuItemRect", classify, StringComparison.Ordinal);
        Assert.DoesNotContain("PopupDiagnostics", source, StringComparison.Ordinal);
    }

    [Fact]
    public void F5OnTheDesktopEntersTheSameRefreshPipelineAsTheMenuItem()
    {
        var mapping = ExtractMethod(
            ReadNativeSource("DesktopDoubleClickMonitor.cs"),
            "private bool TryGetDesktopKeyboardCommand(",
            "private bool CanHandleDesktopCommand(");

        Assert.Contains("private const int VkF5 = 0x74;", ReadNativeSource("DesktopDoubleClickMonitor.cs"), StringComparison.Ordinal);
        Assert.Contains("(uint)VkF5 => DesktopKeyboardCommand.Refresh,", mapping, StringComparison.Ordinal);

        // The switch names the command; the returned predicate decides whether
        // the key is CrabDesk's at all. Mapping F5 without claiming it here
        // would leave the key with Explorer and never raise the command.
        Assert.Contains("virtualKey == (uint)VkF5 ||", mapping, StringComparison.Ordinal);

        var execute = ExtractMethod(
            ReadRuntimeSource("DesktopSurfaceManager.cs"),
            "internal async Task ExecuteDesktopKeyboardCommandAsync(",
            "internal bool BeginRenameSelectedItem()");
        Assert.Contains("case DesktopKeyboardCommand.Refresh:", execute, StringComparison.Ordinal);
        Assert.Contains("_runtime.RequestDesktopRefresh();", execute, StringComparison.Ordinal);

        // One queue for both triggers, so the resort, the debounce and the
        // in-progress coalescing cannot drift apart between them.
        var menuHandler = ExtractMethod(
            ReadRuntimeSource("CrabDeskRuntime.cs"),
            "private void OnDesktopContextMenuRefreshRequested(",
            "public void RequestDesktopRefresh()");
        Assert.Contains("RequestDesktopRefresh();", menuHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("_desktopMenuRefreshTimer.Start();", menuHandler, StringComparison.Ordinal);
    }

    [Fact]
    public void F5IsConsumedWithNothingSelectedUnlikeEveryOtherDesktopKey()
    {
        var source = ReadRuntimeSource("DesktopSurfaceManager.cs");
        var intercept = ExtractMethod(
            source,
            "internal bool CanInterceptDesktopKeyboardCommand(",
            "internal bool CanHandleDesktopKeyboardCommand(");
        var handle = ExtractMethod(
            source,
            "internal bool CanHandleDesktopKeyboardCommand(",
            "internal async Task ExecuteDesktopKeyboardCommandAsync(");

        // Both checks fall through to a selection test — the hook's to
        // HasAnyDesktopSelection(), the UI thread's to false — and a refresh
        // normally happens with nothing selected at all.
        foreach (var check in new[] { intercept, handle })
        {
            Assert.Contains("DesktopKeyboardCommand.Refresh => true,", check, StringComparison.Ordinal);
        }
    }

    private static string ReadRefreshMethod() => ExtractMethod(
        ReadRuntimeSource("CrabDeskRuntime.cs"),
        "private async void RefreshAfterDesktopMenuCommandAsync()",
        "private async void SynchronizeDesktopIconZoom()");

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        for (var index = source.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = source.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string ReadRuntimeSource(string fileName) => ReadSource("CrabDesk.Runtime", fileName);

    private static string ReadNativeSource(string fileName) => ReadSource("CrabDesk.Native", fileName);

    private static string ReadSource(string projectDirectory, string fileName) => File.ReadAllText(
        Path.Combine(FindSolutionDirectory(), projectDirectory, fileName));

    private static string ExtractMethod(string source, string startAnchor, string endAnchor)
    {
        var start = source.IndexOf(startAnchor, StringComparison.Ordinal);
        var end = source.IndexOf(endAnchor, Math.Max(0, start), StringComparison.Ordinal);
        Assert.True(start >= 0, $"{startAnchor} was not found.");
        Assert.True(end > start, $"{endAnchor} was not found after {startAnchor}.");
        return source[start..end];
    }

    private static string FindSolutionDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CrabDesk.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the CrabDesk solution directory.");
    }
}
