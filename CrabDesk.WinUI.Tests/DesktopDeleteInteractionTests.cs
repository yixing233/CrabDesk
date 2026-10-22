using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopDeleteInteractionTests
{
    [Theory]
    [InlineData("DesktopIconSurface.cs")]
    [InlineData("DesktopBoxForm.Input.cs")]
    public void SelectingDesktopIconsRestoresKeyboardInputBeforeHandlingTheSelection(string fileName)
    {
        // Desktop children use WS_EX_NOACTIVATE. After switching from another
        // app, both clicks and marquee selection must activate the desktop root
        // or the global keyboard hook will reject Delete as a non-desktop key.
        var source = File.ReadAllText(Path.Combine(
            FindSolutionDirectory(),
            "CrabDesk.Runtime",
            fileName));
        var mouseDownStart = source.IndexOf(
            "private void OnMouseDown(object? sender, Forms.MouseEventArgs eventArgs)",
            StringComparison.Ordinal);
        Assert.True(mouseDownStart >= 0);
        var firstReturn = source.IndexOf("return;", mouseDownStart, StringComparison.Ordinal);
        var restoreInput = source.IndexOf(
            "_runtime.ActivateDesktopKeyboardInput();",
            mouseDownStart,
            StringComparison.Ordinal);

        Assert.True(restoreInput > mouseDownStart,
            $"{fileName} must restore desktop keyboard input when a selection starts.");
        Assert.True(restoreInput < firstReturn,
            "Keyboard activation must cover both item clicks and blank-area selection.");
    }

    [Fact]
    public void DeleteCompletionRestoresDesktopKeyboardInputAfterClearingBusyState()
    {
        var source = File.ReadAllText(Path.Combine(
            FindSolutionDirectory(),
            "CrabDesk.Runtime",
            "DesktopSurfaceManager.cs"));
        var methodStart = source.IndexOf(
            "internal async Task DeleteSelectedItemsAsync()",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private async Task PasteToDesktopAsync()",
            methodStart,
            StringComparison.Ordinal);
        var method = source[methodStart..methodEnd];
        var clearBusyState = method.IndexOf("_deleteInProgress = false;", StringComparison.Ordinal);
        var restoreInput = method.IndexOf(
            "_runtime.ActivateDesktopKeyboardInput();",
            StringComparison.Ordinal);

        Assert.True(clearBusyState >= 0);
        Assert.True(restoreInput > clearBusyState);
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
