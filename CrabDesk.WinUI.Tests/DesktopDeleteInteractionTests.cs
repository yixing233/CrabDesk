using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopDeleteInteractionTests
{
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
            "private async Task<bool> ConfirmDeleteAsync",
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
