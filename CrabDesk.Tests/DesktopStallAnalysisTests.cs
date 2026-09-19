using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class DesktopStallAnalysisTests
{
    private const int NotMeasured = DesktopStallAnalysis.NotMeasured;

    [Fact]
    public void DesktopStallWithAResponsiveCrabDeskIsBlamedOnShellExtensions()
    {
        // The measurement from the reporting machine: 8 ms idle, 2 ms for an
        // ordinary folder, 1200 ms for the desktop folder, while CrabDesk's own
        // surface answered in 2 ms at the same instant.
        var verdict = DesktopStallAnalysis.ClassifyVerdict(
            desktopFound: true,
            idleBaselineMs: 8,
            ordinaryFolderMs: 2,
            desktopFolderMs: 1200,
            crabDeskSurfaceMs: 2);

        Assert.Equal(DesktopStallVerdict.ThirdPartyShellExtensions, verdict);
    }

    [Fact]
    public void DesktopStallThatAlsoBlocksCrabDeskIsOurOwnBug()
    {
        // Same stall, but CrabDesk waits it out too: the input queues are
        // attached again, which is what the detach fix exists to prevent.
        var verdict = DesktopStallAnalysis.ClassifyVerdict(
            desktopFound: true,
            idleBaselineMs: 8,
            ordinaryFolderMs: 2,
            desktopFolderMs: 1200,
            crabDeskSurfaceMs: 1180);

        Assert.Equal(DesktopStallVerdict.CrabDeskBlocked, verdict);
    }

    [Fact]
    public void AHealthyDesktopThreadIsNotReportedAsAStall()
    {
        var verdict = DesktopStallAnalysis.ClassifyVerdict(
            desktopFound: true,
            idleBaselineMs: 5,
            ordinaryFolderMs: 2,
            desktopFolderMs: 12,
            crabDeskSurfaceMs: 3);

        Assert.Equal(DesktopStallVerdict.NoStall, verdict);
    }

    [Fact]
    public void AMachineWideFreezeIsNotBlamedOnTheDesktopNamespace()
    {
        // Slow everywhere: disk or CPU pressure, not a desktop shell extension.
        // Without the control comparison this would look like a desktop stall.
        var verdict = DesktopStallAnalysis.ClassifyVerdict(
            desktopFound: true,
            idleBaselineMs: 400,
            ordinaryFolderMs: 1100,
            desktopFolderMs: 1200,
            crabDeskSurfaceMs: 20);

        Assert.Equal(DesktopStallVerdict.Inconclusive, verdict);
    }

    [Theory]
    [InlineData(false, 8, 2, 1200, 2)]
    [InlineData(true, 8, NotMeasured, 1200, 2)]
    [InlineData(true, 8, 2, NotMeasured, 2)]
    [InlineData(true, 8, 2, 1200, NotMeasured)]
    public void MissingMeasurementsOrNoDesktopYieldInconclusive(
        bool desktopFound,
        int idle,
        int control,
        int desktop,
        int crabDesk)
    {
        var verdict = DesktopStallAnalysis.ClassifyVerdict(
            desktopFound, idle, control, desktop, crabDesk);

        Assert.Equal(DesktopStallVerdict.Inconclusive, verdict);
    }

    [Fact]
    public void AResponsiveCrabDeskJustAboveTheThresholdStillCountsAsBlocked()
    {
        // The boundary: at or above the responsive threshold the surface is
        // treated as blocked, so a real regression cannot slip through.
        Assert.Equal(
            DesktopStallVerdict.CrabDeskBlocked,
            DesktopStallAnalysis.ClassifyVerdict(true, 8, 2, 1200, DesktopStallAnalysis.ResponsiveThresholdMs));
        Assert.Equal(
            DesktopStallVerdict.ThirdPartyShellExtensions,
            DesktopStallAnalysis.ClassifyVerdict(true, 8, 2, 1200, DesktopStallAnalysis.ResponsiveThresholdMs - 1));
    }

    [Theory]
    [InlineData(@"D:\BaiduNetdisk\YunShellExtV164.dll", "百度网盘")]
    [InlineData(@"D:\Kingsoft\WPS Office\12.1.0\office6\kwpsshellext64.dll", "WPS Office")]
    [InlineData(@"D:\Kingsoft\WPS Office\12.1.0\office6\qingshellext64.dll", "WPS Office")]
    [InlineData(@"D:\Program Files\Bandizip\bdzshl.x64.dll", "Bandizip")]
    [InlineData(@"C:\Program Files\Listary\Hooks\ListaryHook64-6.1.10.1.dll", "Listary")]
    [InlineData(@"C:\ProgramData\Windhawk\Engine\Mods\64\taskbar-grouping.dll", "Windhawk")]
    [InlineData(@"C:\Users\me\.breeze-shell\shell.dll", "breeze-shell")]
    [InlineData(@"C:\Program Files\MI\Xiaomi Cloud\MiCloudShell.dll", "小米云服务")]
    public void RecognizesTheProductsSeenOnTheReportingMachine(string path, string expected)
    {
        Assert.Equal(expected, DesktopStallAnalysis.ClassifyProduct(path));
    }

    [Fact]
    public void UnknownAndMissingPathsFallBackInsteadOfThrowing()
    {
        Assert.Equal("未知", DesktopStallAnalysis.ClassifyProduct(null));
        Assert.Equal("未知", DesktopStallAnalysis.ClassifyProduct("   "));
        Assert.Equal("第三方", DesktopStallAnalysis.ClassifyProduct(@"C:\Tools\SomethingElse\thing.dll"));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\cscui.dll", true)]
    [InlineData(@"C:\Windows\System32\EhStorShell.dll", true)]
    [InlineData(@"C:\Program Files\WindowsApps\Something\app.dll", true)]
    [InlineData(@"D:\BaiduNetdisk\YunShellExtV164.dll", false)]
    [InlineData(@"C:\Program Files\Listary\Hooks\ListaryHook64.dll", false)]
    public void MicrosoftOwnedPathsAreExcludedFromTheSuspectList(string path, bool isMicrosoft)
    {
        Assert.Equal(isMicrosoft, DesktopStallAnalysis.IsMicrosoftOwnedPath(path));
    }

    [Fact]
    public void FiltersOutMicrosoftModulesButKeepsEveryOverlayHandler()
    {
        var entries = new[]
        {
            new ShellExtensionEntry("Offline Files", ShellExtensionKind.IconOverlay, "第三方", @"C:\Windows\System32\cscui.dll"),
            new ShellExtensionEntry(".WorkspaceExt0", ShellExtensionKind.IconOverlay, "百度网盘", @"D:\BaiduNetdisk\YunShellExtV164.dll"),
            new ShellExtensionEntry("ExplorerFrame.dll", ShellExtensionKind.LoadedModule, "第三方", @"C:\Windows\ExplorerFrame.dll"),
            new ShellExtensionEntry("ListaryHook64.dll", ShellExtensionKind.LoadedModule, "Listary", @"C:\Program Files\Listary\ListaryHook64.dll")
        };

        var filtered = DesktopStallAnalysis.FilterRelevantExtensions(entries);

        // Microsoft's own overlay handler stays: it is registered for the
        // desktop namespace, so it is part of the picture even though it is
        // not third-party.
        Assert.Contains(filtered, entry => entry.Name == "Offline Files");
        Assert.Contains(filtered, entry => entry.Name == ".WorkspaceExt0");
        Assert.Contains(filtered, entry => entry.Name == "ListaryHook64.dll");
        // A Windows DLL loaded in Explorer is noise.
        Assert.DoesNotContain(filtered, entry => entry.Name == "ExplorerFrame.dll");
    }

    [Fact]
    public void DuplicateRegistrationsCollapseToASingleRow()
    {
        var entries = new[]
        {
            new ShellExtensionEntry("ListaryHook64.dll", ShellExtensionKind.LoadedModule, "Listary", @"C:\Program Files\Listary\ListaryHook64.dll"),
            new ShellExtensionEntry("ListaryHook64.dll", ShellExtensionKind.LoadedModule, "Listary", @"C:\PROGRAM FILES\LISTARY\ListaryHook64.DLL")
        };

        Assert.Single(DesktopStallAnalysis.FilterRelevantExtensions(entries));
    }

    [Fact]
    public void CountsHandlersPerProductSoTheReportCanSummarizeThem()
    {
        var entries = Enumerable.Range(0, 7)
            .Select(index => new ShellExtensionEntry(
                $".WorkspaceExt{index}",
                ShellExtensionKind.IconOverlay,
                "百度网盘",
                @"D:\BaiduNetdisk\YunShellExtV164.dll"))
            .Append(new ShellExtensionEntry("kwpsshellext64.dll", ShellExtensionKind.LoadedModule, "WPS Office", @"D:\Kingsoft\kwpsshellext64.dll"))
            .ToArray();

        var counts = DesktopStallAnalysis.CountByProduct(entries);

        Assert.Equal(("百度网盘", 7), counts[0]);
        Assert.Equal(("WPS Office", 1), counts[1]);
    }
}
