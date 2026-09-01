using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopStartupTakeoverTests
{
    [Fact]
    public void PreparedDesktopSurfacesAreNotRedrawnAfterConstruction()
    {
        var source = ReadRuntimeSource("CrabDeskRuntime.cs");
        var method = ExtractMethod(
            source,
            "private bool TryRebuildDesktopSurfaces()",
            "private void EnsureDesktopInput");

        Assert.DoesNotContain("_surfaceManager.SetVisible(", method, StringComparison.Ordinal);
        Assert.DoesNotContain("_surfaceManager.Refresh();", method, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacementIconLayerIsComposedOnceBeforeItIsShown()
    {
        var source = ReadRuntimeSource("DesktopSurfaceManager.cs");
        var constructor = ExtractMethod(
            source,
            "internal DesktopSurfaceManager(",
            "// Refreshes every surface after a workspace change");
        var compositionConfigured = constructor.IndexOf(
            "ConfigureBoxIconLayerComposition();",
            StringComparison.Ordinal);
        var iconRendered = constructor.IndexOf(
            "iconSurface.RefreshWorkspace()",
            StringComparison.Ordinal);
        var preparedSurfacesShown = constructor.IndexOf(
            "ShowPreparedSurfaces();",
            StringComparison.Ordinal);
        var explorerIconsHidden = constructor.IndexOf(
            "DesktopWindowTools.TryHideDesktopIconView",
            StringComparison.Ordinal);

        Assert.True(compositionConfigured >= 0);
        Assert.Equal(1, CountOccurrences(constructor, "iconSurface.RefreshWorkspace()"));
        Assert.True(iconRendered > compositionConfigured);
        Assert.True(preparedSurfacesShown > iconRendered);
        Assert.True(explorerIconsHidden > preparedSurfacesShown);
        Assert.Contains("iconSurface.Show();", constructor, StringComparison.Ordinal);
        Assert.DoesNotContain("\n            Refresh();", constructor, StringComparison.Ordinal);
    }

    [Fact]
    public void AppRegistersExitRecoveryForUnexpectedShutdowns()
    {
        var source = File.ReadAllText(Path.Combine(
            FindSolutionDirectory(),
            "CrabDesk.WinUI",
            "App.xaml.cs"));

        Assert.Contains("AppDomain.CurrentDomain.ProcessExit", source, StringComparison.Ordinal);
        Assert.Contains("AppDomain.CurrentDomain.UnhandledException", source, StringComparison.Ordinal);
        Assert.Contains("TryDisposeRuntime(\"Shutdown\")", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopRenderingIsolatesDrawingFailuresFromTheProcess()
    {
        var iconSource = ReadRuntimeSource("DesktopIconSurface.cs");
        var boxSource = ReadRuntimeSource("DesktopBoxForm.cs");

        var iconPresent = ExtractMethod(iconSource, "private bool PresentLayer()", "private bool PresentLayerCore()");
        var boxPresent = ExtractMethod(boxSource, "private bool PresentLayer()", "private void EnsureHitMaskBitmap");
        Assert.Contains("catch (Exception exception)", iconPresent, StringComparison.Ordinal);
        Assert.Contains("catch (Exception exception)", boxPresent, StringComparison.Ordinal);
        Assert.Contains("Desktop icon surface render failed", iconPresent, StringComparison.Ordinal);
        Assert.Contains("Desktop box surface render failed", boxPresent, StringComparison.Ordinal);
    }

    private static string ReadRuntimeSource(string fileName) => File.ReadAllText(Path.Combine(
        FindSolutionDirectory(),
        "CrabDesk.Runtime",
        fileName));

    private static string ExtractMethod(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, Math.Max(0, start), StringComparison.Ordinal);
        Assert.True(start >= 0);
        Assert.True(end > start);
        return source[start..end];
    }

    private static int CountOccurrences(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;

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
