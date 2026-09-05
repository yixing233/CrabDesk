using System.Reflection;
using CrabDesk.Native;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopInputHookLatencyTests
{
    [Fact]
    public void BothLowLevelHooksAreInstalledOnTheirOwnMessageLoopThread()
    {
        var source = ReadNativeSource("DesktopDoubleClickMonitor.cs");
        var constructor = ExtractMethod(
            source,
            "public DesktopInputMonitor()",
            "public event EventHandler<DesktopIconZoomEventArgs>?");
        var loop = ExtractMethod(
            source,
            "private void RunHookMessageLoop()",
            "private void ReleaseHooks()");

        Assert.Contains("SetApartmentState(ApartmentState.STA)", constructor, StringComparison.Ordinal);
        Assert.Contains("_hookThread.Start();", constructor, StringComparison.Ordinal);
        Assert.Contains("SetWindowsHookEx(WhMouseLl", loop, StringComparison.Ordinal);
        Assert.Contains("SetWindowsHookEx(WhKeyboardLl", loop, StringComparison.Ordinal);
        Assert.Contains("while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)", loop, StringComparison.Ordinal);
    }

    [Fact]
    public void DisposeEndsTheHookLoopAndUnhooksExactlyOnce()
    {
        var source = ReadNativeSource("DesktopDoubleClickMonitor.cs");
        var dispose = ExtractMethod(source, "public void Dispose()", "private void RunHookMessageLoop()");
        var release = ExtractMethod(source, "private void ReleaseHooks()", "private IntPtr MouseHook(");

        Assert.Contains("PostThreadMessage(hookThreadId, WmQuit", dispose, StringComparison.Ordinal);
        Assert.Contains("_hookThread.Join(HookShutdownTimeoutMilliseconds)", dispose, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _mouseHook, IntPtr.Zero)", release, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _keyboardHook, IntPtr.Zero)", release, StringComparison.Ordinal);
    }

    [Fact]
    public void PointerMovesAreRejectedBeforeAnyWindowHitTest()
    {
        var source = ReadNativeSource("DesktopDoubleClickMonitor.cs");
        var callback = ExtractMethod(
            source,
            "private IntPtr MouseHook(int code, IntPtr message, IntPtr data)",
            "private static bool IsHandledMouseMessage(int message)");

        Assert.Contains("IsHandledMouseMessage(msg)", callback, StringComparison.Ordinal);
        Assert.Contains("catch (Exception)", callback, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowFromPoint", callback, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x0201, true)]
    [InlineData(0x0204, true)]
    [InlineData(0x020A, true)]
    [InlineData(0x0200, false)]
    [InlineData(0x0202, false)]
    public void OnlyButtonDownAndWheelMessagesReachTheMouseHandler(int message, bool expected)
    {
        var method = typeof(DesktopInputMonitor).GetMethod(
            "IsHandledMouseMessage",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        Assert.Equal(expected, (bool)method!.Invoke(null, [message])!);
    }

    [Fact]
    public void AnOrdinaryWheelScrollSkipsThePointerHitTest()
    {
        var handler = ExtractMethod(
            ReadNativeSource("DesktopDoubleClickMonitor.cs"),
            "private bool HandleMouseMessage(",
            "internal static bool ShouldRouteBoxDragWheel(");
        var dragProbe = handler.IndexOf("var dragActive = IsBoxItemDragActive?.Invoke() == true", StringComparison.Ordinal);
        var earlyExit = handler.IndexOf("if (!controlPressed && !dragActive)", StringComparison.Ordinal);
        var hitTest = handler.IndexOf("var overBox = IsPointerOverBox?.Invoke(", StringComparison.Ordinal);

        Assert.True(dragProbe >= 0);
        Assert.True(earlyExit > dragProbe);
        Assert.True(hitTest > earlyExit);
    }

    [Fact]
    public void TheKeyboardHookAsksTheCheapInterceptionCheck()
    {
        var configure = ExtractMethod(
            ReadRuntimeSource("CrabDeskRuntime.cs"),
            "private void ConfigureDesktopInputMonitor()",
            "private void OnDesktopIconZoomRequested(");

        Assert.Contains(
            "_surfaceManager?.CanInterceptDesktopKeyboardCommand(command) == true",
            configure,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CanHandleDesktopKeyboardCommand(command)", configure, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInterceptionCheckReadsAtomicStateOnly()
    {
        var source = ReadRuntimeSource("DesktopSurfaceManager.cs");
        var intercept = ExtractMethod(
            source,
            "internal bool CanInterceptDesktopKeyboardCommand(",
            "internal bool CanHandleDesktopKeyboardCommand(");

        Assert.Contains("_runtime.CanPasteToDesktop()", intercept, StringComparison.Ordinal);
        Assert.Contains("HasAnyDesktopSelection()", intercept, StringComparison.Ordinal);
        Assert.DoesNotContain("GetPasteTargetSurface(", intercept, StringComparison.Ordinal);
        Assert.DoesNotContain("GetSelectedItems(", intercept, StringComparison.Ordinal);
        Assert.DoesNotContain("GetSelectedFileSystemItems(", intercept, StringComparison.Ordinal);
        Assert.DoesNotContain("GetDeleteSelection(", intercept, StringComparison.Ordinal);
        Assert.Contains(
            "_iconSurfaces.Any(surface => surface.HasSelection)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheUiThreadStillRepeatsTheExactCheckBeforeItActs()
    {
        var execute = ExtractMethod(
            ReadRuntimeSource("DesktopSurfaceManager.cs"),
            "internal async Task ExecuteDesktopKeyboardCommandAsync(",
            "internal bool BeginRenameSelectedItem()");

        Assert.Contains("if (!CanHandleDesktopKeyboardCommand(command))", execute, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePasteCheckOnlyInspectsTheOfferedClipboardFormats()
    {
        var canPaste = ExtractMethod(
            ReadRuntimeSource("CrabDeskRuntime.cs"),
            "public bool CanPasteToDesktop()",
            "public async Task<FileImportBatchResult> PasteToDesktopAsync()");

        Assert.Contains("_fileOperations.HasClipboardFiles()", canPaste, StringComparison.Ordinal);
        Assert.DoesNotContain("GetClipboardFiles()", canPaste, StringComparison.Ordinal);
        Assert.Contains(
            "IsClipboardFormatAvailable(NativeMethods.CfHdrop)",
            ReadNativeSource("FileOperationService.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheHookSafeBoxHitTestDoesNotMutateAnimationState()
    {
        var hitTest = ExtractMethod(
            ReadRuntimeSource("DesktopBoxForm.cs"),
            "private DesktopBox? GetBoxAtScreenPoint(Point screenPoint)",
            "internal bool IsPointOverBox(Point screenPoint)");
        var settledHeight = ExtractMethod(
            ReadRuntimeSource("DesktopBoxForm.TitleEditor.cs"),
            "private double GetSettledBoxHeight(DesktopBox box) =>",
            "private double GetVisualBoxHeight(DesktopBox box)");

        Assert.Contains("GetSettledBoxHeight(box)", hitTest, StringComparison.Ordinal);
        Assert.DoesNotContain("GetVisualBoxHeight(", hitTest, StringComparison.Ordinal);
        Assert.DoesNotContain("_heightAnimations", settledHeight, StringComparison.Ordinal);
        Assert.DoesNotContain("ReleaseHeightAnimationVisualCache", settledHeight, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderPathsReadTheSharedShellViewSnapshot()
    {
        var rebuild = ExtractMethod(
            ReadRuntimeSource("DesktopIconSurface.cs"),
            "private void RebuildGeometry()",
            "private DesktopIconLayoutSnapshot? GetStoredLayoutPlacement(");
        var metrics = ExtractMethod(
            ReadRuntimeSource("DesktopIconSurface.cs"),
            "private void SynchronizeNativeMetrics(",
            "private static IOrderedEnumerable<DesktopItemRef> OrderDesktopItems(");

        Assert.Contains(
            "DesktopIconPositionService.GetCachedDesktopViewState()",
            rebuild,
            StringComparison.Ordinal);
        Assert.Contains(
            "DesktopIconPositionService.TryGetCachedItemSpacing(_desktopListView, out var nativeSpacing)",
            metrics,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CachedDesktopViewReadsNeverCrossIntoExplorer()
    {
        var cachedRead = ExtractMethod(
            ReadNativeSource("DesktopIconPositionService.cs"),
            "public static DesktopIconViewState GetCachedDesktopViewState()",
            "public static void InvalidateCachedDesktopView()");

        Assert.DoesNotContain("GetDesktopViewState()", cachedRead, StringComparison.Ordinal);
        Assert.Contains("ReadPersistedDesktopViewState()", cachedRead, StringComparison.Ordinal);
    }

    [Fact]
    public void HostHealthTickDoesNotPollExplorersAutomationView()
    {
        var hostTimer = ExtractMethod(
            ReadRuntimeSource("CrabDeskRuntime.cs"),
            "private async void OnHostTimer(",
            "private void ConfigureDesktopInputMonitor()");

        Assert.DoesNotContain("GetDesktopViewState", hostTimer, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetItemSpacing", hostTimer, StringComparison.Ordinal);
    }

    [Fact]
    public void HostHealthNativeProbeDoesNotRunOnTheUiThread()
    {
        var hostTimer = ExtractMethod(
            ReadRuntimeSource("CrabDeskRuntime.cs"),
            "private async void OnHostTimer(",
            "private void OnUiHeartbeat()");

        Assert.Contains("await Task.Run(() =>", hostTimer, StringComparison.Ordinal);
        Assert.Contains("DesktopHostService.Probe()", hostTimer, StringComparison.Ordinal);
        Assert.Contains("_desktopHost.Apply(healthSnapshot.Host)", hostTimer, StringComparison.Ordinal);
        Assert.Contains("DesktopItemProvider.GetSystemDesktopIconVisibilitySignature()", hostTimer, StringComparison.Ordinal);
        Assert.Contains("_monitorService.GetMonitors()", hostTimer, StringComparison.Ordinal);
    }

    [Fact]
    public void BoxWheelIsHandledBeforeItCanBubbleIntoExplorer()
    {
        var wheelHandler = ExtractMethod(
            ReadRuntimeSource("DesktopBoxForm.Input.cs"),
            "private void OnMouseWheel(",
            "internal bool TryScrollBoxAt(");

        Assert.Contains("eventArgs is Forms.HandledMouseEventArgs", wheelHandler, StringComparison.Ordinal);
        Assert.Contains("handledEventArgs.Handled = true;", wheelHandler, StringComparison.Ordinal);
    }

    [Fact]
    public void HostProbeDoesNotMutateTheLiveHostOffTheUiThread()
    {
        var hostTimer = ExtractMethod(
            ReadRuntimeSource("CrabDeskRuntime.cs"),
            "private async void OnHostTimer(",
            "private void OnUiHeartbeat()");
        var taskRun = ExtractMethod(
            hostTimer,
            "var healthSnapshot = await Task.Run(() =>",
            "if (probeStarted.ElapsedMilliseconds >= 100)");

        Assert.Contains("DesktopHostService.Probe()", taskRun, StringComparison.Ordinal);
        Assert.DoesNotContain("_desktopHost.Refresh()", taskRun, StringComparison.Ordinal);
        Assert.DoesNotContain("_desktopHost.Apply", taskRun, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupWarmsShellViewAndSpacingAwayFromTheUiThread()
    {
        var initialize = ExtractMethod(
            ReadRuntimeSource("CrabDeskRuntime.cs"),
            "public async Task InitializeAsync()",
            "private DesktopIconViewState ReadInitialDesktopShellState()");
        var warmup = ExtractMethod(
            ReadRuntimeSource("CrabDeskRuntime.cs"),
            "private DesktopIconViewState ReadInitialDesktopShellState()",
            "public IReadOnlyList<DesktopItemRef> GetItemsForBox(");

        Assert.Contains("await Task.Run(ReadInitialDesktopShellState)", initialize, StringComparison.Ordinal);
        Assert.Contains("DesktopIconPositionService.GetDesktopViewState()", warmup, StringComparison.Ordinal);
        Assert.Contains("DesktopIconPositionService.TryGetItemSpacing", warmup, StringComparison.Ordinal);
    }

    [Fact]
    public void CachedSpacingReadsNeverSendMessagesToExplorer()
    {
        var cachedRead = ExtractMethod(
            ReadNativeSource("DesktopIconPositionService.cs"),
            "public static bool TryGetCachedItemSpacing(",
            "public static bool TryGetItemSpacing(");

        Assert.DoesNotContain("TryGetItemSpacing(listView", cachedRead, StringComparison.Ordinal);
        Assert.DoesNotContain("SendMessageTimeout", cachedRead, StringComparison.Ordinal);
    }

    [Fact]
    public void BoxShellThumbnailExtractionLeavesTheUiThreadBeforeEnteringNativeCode()
    {
        var loadIcon = ExtractMethod(
            ReadRuntimeSource("DesktopBoxForm.Icons.cs"),
            "private async Task LoadIconBitmapAsync(",
            "private void ScheduleIconLoadRetry(");

        Assert.Contains("await Task.Run(() =>", loadIcon, StringComparison.Ordinal);
        Assert.Contains("_runtime.IconProvider.GetIcon", loadIcon, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopRefreshRetainsExistingIconsAndBoundsShellLoadConcurrency()
    {
        var source = ReadRuntimeSource("DesktopIconSurface.cs");
        var refresh = ExtractMethod(
            source,
            "internal bool RefreshWorkspace()",
            "internal bool RefreshReleasedItems(");
        var loadIcon = ExtractMethod(
            source,
            "private async Task LoadDesktopIconAsync(",
            "private static (string ParsingName, int PixelSize, long ModifiedTicks) CreateDesktopIconCacheKey(");

        Assert.DoesNotContain("ClearDesktopIconCache()", refresh, StringComparison.Ordinal);
        Assert.Contains("PruneDesktopIconCache()", refresh, StringComparison.Ordinal);
        Assert.Contains("await _desktopIconLoadGate.WaitAsync", loadIcon, StringComparison.Ordinal);
        Assert.Contains("_desktopIconLoadGate.Release()", loadIcon, StringComparison.Ordinal);
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
