using Xunit;

namespace CrabDesk.WinUI.Tests;

/// <summary>
/// Pins the instrumentation and the serialization that a multi-second UI-thread
/// stall depends on. The stall itself happens outside every timed path, so these
/// invariants are what make the next occurrence diagnosable instead of silent.
/// </summary>
public sealed class DesktopUiThreadStallTests
{
    [Fact]
    public void TheWatchdogWatchesFromItsOwnThreadSoPoolStarvationCannotHideAStall()
    {
        var source = ReadRuntimeSource("UiThreadWatchdog.cs");
        var start = ExtractMethod(source, "internal static void Start()", "private static void Watch()");
        var watch = ExtractMethod(source, "private static void Watch()", "internal readonly struct ActivityScope");

        Assert.Contains("new Thread(Watch)", start, StringComparison.Ordinal);
        Assert.Contains("IsBackground = true", start, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _started, 1) != 0", start, StringComparison.Ordinal);
        Assert.Contains("Thread.Sleep(SampleInterval)", watch, StringComparison.Ordinal);
        Assert.Contains("UI thread stall began scope=", watch, StringComparison.Ordinal);
        Assert.Contains("UI thread stall ended stalledMs=", watch, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run", watch, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Delay", watch, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRuntimeStartsTheWatchdogAndNamesEveryTimerItDispatches()
    {
        var constructor = ExtractMethod(
            ReadRuntimeSource("CrabDeskRuntime.cs"),
            "public CrabDeskRuntime(Action<Action> beginInvoke)",
            "private void BeginInvoke(string name, Action action)");

        Assert.Contains("UiThreadWatchdog.Start();", constructor, StringComparison.Ordinal);
        Assert.Contains("\"host timer\"", constructor, StringComparison.Ordinal);
        Assert.Contains("\"ui heartbeat\"", constructor, StringComparison.Ordinal);
        Assert.Contains("\"autosave\"", constructor, StringComparison.Ordinal);
        Assert.Contains("\"desktop zoom sync\"", constructor, StringComparison.Ordinal);
        Assert.Contains("\"explorer view sync\"", constructor, StringComparison.Ordinal);
        Assert.Contains("\"menu refresh\"", constructor, StringComparison.Ordinal);
        Assert.Contains("BeginInvoke(\"desktop items changed\"", constructor, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryUiDispatchLeavesABreadcrumbInsteadOfCallingTheRawDispatcher()
    {
        var source = ReadRuntimeSource("CrabDeskRuntime.cs");
        var helper = ExtractMethod(
            source,
            "private void BeginInvoke(string name, Action action)",
            "public event EventHandler? Changed;");

        Assert.Contains("UiThreadWatchdog.Enter(name)", helper, StringComparison.Ordinal);

        // _beginInvoke may only appear as the field, its assignment and this one
        // wrapper. Any other call site would dispatch work the watchdog cannot name.
        var rawDispatches = CountOccurrences(source, "_beginInvoke");
        Assert.Equal(3, rawDispatches);
    }

    [Fact]
    public void ADispatchedTimerTickReportsQueueTimeSeparatelyFromPoolDelay()
    {
        // RuntimeTimer.cs holds only the timer, so the whole file is the method here.
        var timer = ReadRuntimeSource("RuntimeTimer.cs");

        Assert.Contains("var enqueuedAt = Stopwatch.GetTimestamp();", timer, StringComparison.Ordinal);
        Assert.Contains(
            "Stopwatch.GetElapsedTime(previousPoolTimestamp, enqueuedAt).TotalMilliseconds",
            timer,
            StringComparison.Ordinal);
        Assert.Contains("Stopwatch.GetElapsedTime(enqueuedAt).TotalMilliseconds", timer, StringComparison.Ordinal);
        Assert.Contains("UiThreadWatchdog.Enter(_name)", timer, StringComparison.Ordinal);

        var heartbeat = ExtractMethod(
            ReadRuntimeSource("CrabDeskRuntime.cs"),
            "private void OnUiHeartbeat()",
            "private static bool MappedSnapshotsEqual(");

        Assert.Contains("UiThreadWatchdog.ReportAlive();", heartbeat, StringComparison.Ordinal);
        Assert.Contains("poolIntervalMs={latency.PoolIntervalMs:0}", heartbeat, StringComparison.Ordinal);
        Assert.Contains("dispatchMs={latency.DispatchMs:0}", heartbeat, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowMessageHandlingIsAlsoNamedBecauseNoDispatchWrapperSeesIt()
    {
        Assert.Contains(
            "UiThreadWatchdog.EnterWindowMessage(\"box window\", diagnosticMessage)",
            ReadRuntimeSource("DesktopBoxForm.cs"),
            StringComparison.Ordinal);
        Assert.Contains(
            "UiThreadWatchdog.EnterWindowMessage(\"icon window\", diagnosticMessage)",
            ReadRuntimeSource("DesktopIconSurface.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void NeitherDesktopSurfaceLetsAButtonPressReachExplorersWindowChain()
    {
        // WM_PARENTNOTIFY travels to the cross-process Explorer parent
        // synchronously, so a busy desktop thread would block CrabDesk's input.
        Assert.Contains(
            "parameters.ExStyle |= WsExLayered | WsExNoParentNotify;",
            ReadRuntimeSource("DesktopBoxForm.cs"),
            StringComparison.Ordinal);
        Assert.Contains(
            "parameters.ExStyle |= WsExLayered | WsExNoParentNotify;",
            ReadRuntimeSource("DesktopIconSurface.cs"),
            StringComparison.Ordinal);

        var normalize = ExtractMethod(
            ReadNativeSource("DesktopWindowTools.cs"),
            "private static void NormalizeDesktopSurfaceStyles(IntPtr hwnd)",
            "public static long GetSurfaceExtendedStyle(IntPtr hwnd)");

        Assert.Contains(
            "NativeMethods.WsExToolWindow | WsExNoActivate | WsExNoParentNotify;",
            normalize,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ActivatingExplorerForKeyboardInputNeverWaitsOnABusyWindowFromTheUiThread()
    {
        // Every icon click makes Explorer's desktop root the foreground window so F2
        // and friends reach the desktop. SetForegroundWindow waits synchronously on
        // the window being deactivated; after a drop from Explorer that is the
        // source window, still busy refreshing, and CrabDesk's UI thread sat there
        // for up to 8 s (watchdog: scope=icon window msg=0x0201). The call must run
        // off the UI thread, coalesced, and the click handler must time its stages.
        var activate = ExtractMethod(
            ReadRuntimeSource("CrabDeskRuntime.cs"),
            "internal void ActivateDesktopKeyboardInput()",
            "// The icon surface owns the pointer-captured desktop drag.");
        Assert.Contains("ThreadPool.UnsafeQueueUserWorkItem(", activate, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _desktopInputActivationPending, 1)", activate, StringComparison.Ordinal);
        Assert.Contains("Desktop input activation slow", activate, StringComparison.Ordinal);
        // The native call itself stays in DesktopWindowTools; the runtime only ever reaches it from the pool.
        Assert.DoesNotContain("NativeMethods.SetForegroundWindow", activate, StringComparison.Ordinal);

        var native = ExtractMethod(
            ReadNativeSource("DesktopWindowTools.cs"),
            "public static bool TryActivateDesktopInput(IntPtr root)",
            "public static void ToggleDesktop()");
        Assert.Contains("GetForegroundWindow() == root", native, StringComparison.Ordinal);

        var mouseDown = ExtractMethod(
            ReadRuntimeSource("DesktopIconSurface.cs"),
            "private void OnMouseDown(object? sender, Forms.MouseEventArgs eventArgs)",
            "$\"Icon surface mouse down monitor=");
        Assert.Contains("Slow desktop mouse down stages", mouseDown, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopSurfacesDetachTheirInputQueueFromExplorersThread()
    {
        // A cross-thread SetParent attaches the two input queues. Explorer's desktop
        // thread blocks for seconds inside a COM activation whenever a file lands on
        // the desktop (third-party shell extensions; reproduced with CrabDesk exited),
        // and with attached queues the first click on our child window waited on it.
        var tools = ReadNativeSource("DesktopWindowTools.cs");
        var attach = ExtractMethod(
            tools,
            "public static void AttachAsDesktopChild(IntPtr hwnd, IntPtr desktopParent)",
            "public static bool DetachInputQueueFromParentThread(");
        var setParent = attach.IndexOf("NativeMethods.SetParent(hwnd, desktopParent);", StringComparison.Ordinal);
        var detach = attach.IndexOf("DetachInputQueueFromParentThread(hwnd, desktopParent);", StringComparison.Ordinal);
        Assert.True(setParent >= 0 && detach > setParent, "input queues must be detached right after SetParent");
        Assert.Contains("NativeMethods.AttachThreadInput(childThread, parentThread, false)", tools, StringComparison.Ordinal);

        // Detached queues no longer mirror Explorer's key state, so modifier reads must
        // come from GetAsyncKeyState instead of Control.ModifierKeys (GetKeyState).
        foreach (var file in new[] { "DesktopIconSurface.cs", "DesktopBoxForm.Input.cs", "DesktopBoxForm.cs", "DesktopBoxForm.DragDrop.cs", "DesktopSurfaceManager.cs" })
        {
            Assert.DoesNotContain("Control.ModifierKeys", ReadRuntimeSource(file), StringComparison.Ordinal);
        }
        Assert.Contains("GetAsyncKeyState(vkControl)", tools, StringComparison.Ordinal);
    }

    [Fact]
    public void APasteBurstQueuesInsteadOfRunningSeveralPastesAtOnce()
    {
        var source = ReadRuntimeSource("CrabDeskRuntime.cs");
        var desktopPaste = ExtractMethod(
            source,
            "public async Task<FileImportBatchResult> PasteToDesktopAsync()",
            "private async Task<FileImportBatchResult> PasteToDesktopCoreAsync()");
        var boxPaste = ExtractMethod(
            source,
            "public async Task<BoxPasteResult> PasteIntoBoxAsync(Guid boxId, Guid? targetTabId = null)",
            "private async Task<BoxPasteResult> PasteIntoBoxCoreAsync(Guid boxId, Guid? targetTabId)");

        foreach (var wrapper in new[] { desktopPaste, boxPaste })
        {
            Assert.Contains("await _pasteGate.WaitAsync();", wrapper, StringComparison.Ordinal);
            Assert.Contains("_pasteGate.Release();", wrapper, StringComparison.Ordinal);
            Assert.Contains("finally", wrapper, StringComparison.Ordinal);
        }

        // One gate for both entry points: each paste reads the clipboard and
        // enumerates the desktop, so a box paste must not overlap a desktop one.
        Assert.Contains("private readonly SemaphoreSlim _pasteGate = new(1, 1);", source, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(source, "await _pasteGate.WaitAsync();"));

        // The gate must never be taken by blocking the UI thread.
        Assert.DoesNotContain("_pasteGate.Wait()", source, StringComparison.Ordinal);
    }

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
