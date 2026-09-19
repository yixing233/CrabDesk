using CrabDesk.Native;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

/// <summary>
/// Runs the real stall measurement against the live desktop. It is the only
/// test that exercises the probe-file lifecycle and the cross-process message
/// round trips end to end; everything else about the feature is pure logic.
/// </summary>
/// <remarks>
/// These tests write a probe file to the real desktop and Documents folder, so
/// they must not run in parallel with each other: two concurrent measurements
/// would each be reacting to the other's file and the counts would race.
/// </remarks>
[Collection("desktop-probe")]
public sealed class DesktopStallDiagnosticsEndToEndTests
{
    [Fact]
    public void MeasuringAgainstTheLiveDesktopLeavesNoProbeFilesBehind()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var desktopView = DesktopHostService.FindDesktopView();
        if (desktopView == IntPtr.Zero)
        {
            // No interactive desktop (headless test host): nothing to measure.
            return;
        }

        var desktopDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var controlDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var before = CountProbeFiles(desktopDirectory, controlDirectory);

        var diagnostics = new DesktopStallDiagnostics(desktopView, crabDeskSurface: IntPtr.Zero);
        var report = diagnostics.Run();

        Assert.True(report.DesktopFound);
        Assert.NotNull(report.Notes);
        Assert.Equal(before, CountProbeFiles(desktopDirectory, controlDirectory));
    }

    [Fact]
    public void WithNoCrabDeskSurfaceTheReportIsInconclusiveRatherThanWrong()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var desktopView = DesktopHostService.FindDesktopView();
        if (desktopView == IntPtr.Zero)
        {
            return;
        }

        // No CrabDesk window to compare against: the verdict must refuse to
        // attribute the stall instead of claiming shell extensions are at fault.
        var diagnostics = new DesktopStallDiagnostics(desktopView, crabDeskSurface: IntPtr.Zero);
        var report = diagnostics.Run();

        Assert.Equal(Core.DesktopStallVerdict.Inconclusive, report.Verdict);
    }

    [Fact]
    public void NoDesktopViewYieldsAnInconclusiveReportWithAnExplanation()
    {
        var diagnostics = new DesktopStallDiagnostics(IntPtr.Zero, IntPtr.Zero);

        var report = diagnostics.Run();

        Assert.False(report.DesktopFound);
        Assert.Equal(Core.DesktopStallVerdict.Inconclusive, report.Verdict);
        Assert.Contains(report.Notes, note => note.Contains("未找到桌面窗口", StringComparison.Ordinal));
    }

    private static int CountProbeFiles(params string[] directories) => directories
        .Where(Directory.Exists)
        .SelectMany(directory => Directory.EnumerateFiles(directory, "CrabDesk-stall-probe-*"))
        .Count();
}
