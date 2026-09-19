using Xunit;

namespace CrabDesk.WinUI.Tests;

/// <summary>
/// Guards the properties of the stall diagnostics that a unit test cannot
/// reach: that the measurement stays off the UI thread, that the probe file is
/// always cleaned up, and that the two windows really are sampled alternately.
/// </summary>
public sealed class DesktopStallDiagnosticsSourceTests
{
    [Fact]
    public void TheMeasurementNeverRunsOnTheUiThread()
    {
        var source = ReadRuntimeSource("DesktopStallDiagnostics.cs");
        var runAsync = ExtractMethod(
            source,
            "internal Task<DesktopStallReport> RunAsync(",
            "internal DesktopStallReport Run(");

        // Creating files and sending cross-process messages would freeze the
        // desktop if it ran inline; the UI thread is what draws the boxes.
        Assert.Contains("Task.Run(", runAsync, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryProbeFileIsRemovedInAFinallyBlock()
    {
        var source = ReadRuntimeSource("DesktopStallDiagnostics.cs");

        // Both probe files, the control folder's and the desktop folder's, must
        // be deleted in a finally: leaving either behind would put a stray file
        // on the user's desktop, and the desktop one is the visible case.
        var controlProbe = ExtractMethod(
            source,
            "private static StallSample MeasureWithProbeFile(",
            "/// <summary>\n    /// Creates the probe file");
        Assert.Contains("finally", controlProbe, StringComparison.Ordinal);
        Assert.Contains("DeleteProbeFile(probeFile);", controlProbe, StringComparison.Ordinal);

        var desktopProbe = ExtractMethod(
            source,
            "var desktopFile = CreateProbeFile(desktopDirectory);",
            "notes.Add(probeNote);");
        Assert.Contains("finally", desktopProbe, StringComparison.Ordinal);
        Assert.Contains("DeleteProbeFile(desktopFile);", desktopProbe, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProbeFileIsAVisibleFileSoExplorersOverlayHandlersActuallyRun()
    {
        var source = ReadRuntimeSource("DesktopStallDiagnostics.cs");
        var create = ExtractMethod(
            source,
            "private static string? CreateProbeFile(",
            "private static void DeleteProbeFile(");

        // A hidden file is filtered out of Explorer's view, so the overlay
        // handlers that cause the stall would never run and the measurement
        // would silently report a healthy desktop.
        Assert.DoesNotContain("FileAttributes.Hidden", create, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteOnClose", create, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTwoWindowsAreSampledAlternatelyRatherThanOneAfterTheOther()
    {
        var probe = ReadNativeSource("DesktopStallProbe.cs");
        var paired = ExtractMethod(
            probe,
            "public static PairedStallSample MeasurePairedWorstResponse(",
            "private static (int ElapsedMs, bool Ok) SendNull(");

        // Both SendNull calls must sit inside one loop body. Measuring one
        // window to completion and then the other would let the first window's
        // stall end before the second is sampled, making a blocked window look
        // responsive, which is exactly the false negative this guards.
        var loop = paired.IndexOf("for (var i = 0;", StringComparison.Ordinal);
        Assert.True(loop >= 0, "the paired sampler must loop over rounds");
        var firstSend = paired.IndexOf("SendNull(first,", StringComparison.Ordinal);
        var secondSend = paired.IndexOf("SendNull(second,", StringComparison.Ordinal);
        Assert.True(firstSend > loop, "the first window must be sampled inside the round loop");
        Assert.True(secondSend > firstSend, "the second window must be sampled in the same round");
    }

    [Fact]
    public void TheVerdictComesFromTheSharedAnalysisRatherThanLocalThresholds()
    {
        var source = ReadRuntimeSource("DesktopStallDiagnostics.cs");

        // Keeping one copy of the thresholds in Core is what makes the verdict
        // testable without a desktop; a second copy here would drift.
        Assert.Contains("DesktopStallAnalysis.ClassifyVerdict(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ResponsiveThresholdMs =", source, StringComparison.Ordinal);
        Assert.DoesNotContain("StallThresholdMs =", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDiagnosticsPageIsReachableFromTheNavigation()
    {
        var navigation = ReadSource("CrabDesk.WinUI", "MainWindow.xaml");
        var routing = ReadSource("CrabDesk.WinUI", "MainWindow.xaml.cs");

        Assert.Contains("Tag=\"diagnostics\"", navigation, StringComparison.Ordinal);
        Assert.Contains("\"diagnostics\" => typeof(DiagnosticsPage)", routing, StringComparison.Ordinal);
    }

    [Fact]
    public void TheViewModelIsRegisteredForThePageToResolve()
    {
        var app = ReadSource("CrabDesk.WinUI", "App.xaml.cs");

        Assert.Contains("AddTransient<DiagnosticsViewModel>()", app, StringComparison.Ordinal);
    }

    [Fact]
    public void DisablingThirdPartyExtensionsIsNotSomethingTheAppDoesForTheUser()
    {
        var inventory = ReadNativeSource("ShellExtensionInventory.cs");
        var diagnostics = ReadRuntimeSource("DesktopStallDiagnostics.cs");

        // The report names the culprit; renaming an HKLM value would need
        // elevation and would change another product's registration, so the
        // decision stays with the user.
        Assert.DoesNotContain("SetValue", inventory, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteSubKey", inventory, StringComparison.Ordinal);
        Assert.DoesNotContain("SetValue", diagnostics, StringComparison.Ordinal);
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

        throw new InvalidOperationException("CrabDesk.sln was not found above the test output directory.");
    }
}
