using Xunit;

namespace CrabDesk.WinUI.Tests;

/// <summary>
/// Pins that an explicit desktop Refresh — the context-menu item or F5 — re-lays
/// the icons out in Explorer's active sort order, and that nothing else does.
/// Neither end of that contract can be reached without a live shell: the
/// triggers are an Explorer context-menu command and a low-level keyboard hook,
/// and the effect is a grid rebuild, so both are asserted on the source.
/// </summary>
public sealed class DesktopRefreshSortTests
{
    [Fact]
    public void ARefreshDropsTheManualGridSoEveryIconComesBackInTheActiveSortOrder()
    {
        var method = ReadRefreshMethod();

        var flag = method.IndexOf("_desktopRefreshResortPending = true;", StringComparison.Ordinal);
        var reset = method.IndexOf(
            "ResetDesktopIconLayoutForAutoArrange(refreshWorkspace: false);",
            StringComparison.Ordinal);
        var refresh = method.IndexOf("await RefreshItemsAsync(false);", StringComparison.Ordinal);

        Assert.True(flag >= 0, "The refresh must announce that this rebuild re-sorts.");
        Assert.True(reset > flag, "The saved grid must be dropped before the rebuild reads it.");
        Assert.True(refresh > reset, "Clearing the layout after the rebuild would come one pass too late.");

        // refreshWorkspace: true would refresh the surfaces a second time, once
        // with the layout already gone and once through RefreshItemsAsync.
        Assert.DoesNotContain(
            "ResetDesktopIconLayoutForAutoArrange();",
            method,
            StringComparison.Ordinal);
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

        Assert.Contains("!_runtime.IsDesktopResortPending;", rebuild, StringComparison.Ordinal);

        // With no stored cells left, every item takes the auto-placement path,
        // which is the one that honours the active sort property.
        var storedLayout = rebuild.IndexOf("var storedLayout = useStoredLayout", StringComparison.Ordinal);
        var ordering = rebuild.IndexOf("desktopViewState.Sort))", StringComparison.Ordinal);
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
            "internal void RequestDesktopRefresh()");
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
