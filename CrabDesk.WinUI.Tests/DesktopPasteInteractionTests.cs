using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopPasteInteractionTests
{
    [Theory]
    [InlineData(@"C:\Users\Test\Downloads\image.png", false, true)]
    [InlineData(@"C:\Users\Test\Desktop\image.png", false, true)]
    [InlineData(@"C:\Users\Test\Downloads\image.png", true, true)]
    [InlineData(@"C:\Users\Test\Desktop\image.png", true, false)]
    [InlineData(@"C:\Users\Test\Desktop\\image.png", true, false)]
    public void DesktopPasteSkipsCutSourcesThatAlreadyLiveOnTheDesktop(
        string sourcePath,
        bool move,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopPastePolicy.CanPasteSource(
                sourcePath,
                @"C:\Users\Test\Desktop",
                move));
    }

    [Fact]
    public void DesktopKeyboardPasteFallsBackToTheDesktopWhenNoBoxIsTargeted()
    {
        var source = File.ReadAllText(Path.Combine(
            FindSolutionDirectory(),
            "CrabDesk.Runtime",
            "DesktopSurfaceManager.cs"));
        var caseStart = source.IndexOf(
            "case DesktopKeyboardCommand.Paste:",
            StringComparison.Ordinal);
        var caseEnd = source.IndexOf(
            "case DesktopKeyboardCommand.Open:",
            Math.Max(0, caseStart),
            StringComparison.Ordinal);

        Assert.True(caseStart >= 0);
        Assert.True(caseEnd > caseStart);
        var pasteCase = source[caseStart..caseEnd];
        Assert.Contains("PasteIntoSelectedOrHoveredBoxAsync(", pasteCase, StringComparison.Ordinal);
        Assert.Contains("await PasteToDesktopAsync();", pasteCase, StringComparison.Ordinal);
        Assert.Contains(
            "_runtime.CanPasteToDesktop()",
            source,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\Media\Pictures\image.png", false, true)]
    [InlineData(@"C:\Media\Pictures\image.png", true, false)]
    [InlineData(@"C:\Media\Downloads\image.png", true, true)]
    public void MappedBoxPasteSkipsCutSourcesThatAlreadyLiveInTheMappedFolder(
        string sourcePath,
        bool move,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopPastePolicy.CanPasteSource(
                sourcePath,
                @"C:\Media\Pictures",
                move));
    }

    [Fact]
    public void CopyingAnExistingDesktopItemIntoABoxDuplicatesItInsteadOfReassigningIt()
    {
        var source = File.ReadAllText(Path.Combine(
            FindSolutionDirectory(),
            "CrabDesk.Runtime",
            "CrabDeskRuntime.cs"));
        var methodStart = source.IndexOf(
            "public async Task<BoxPasteResult> PasteIntoBoxAsync(Guid boxId, Guid? targetTabId = null)",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "public async Task RenameItemAsync(",
            Math.Max(0, methodStart),
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        Assert.Contains(
            "if (clipboard.Move && desktopItems.TryGetValue(fullPath, out var item))",
            method,
            StringComparison.Ordinal);
        Assert.Contains(
            "DesktopPastePolicy.CanPasteSource(",
            method,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "if (desktopItems.TryGetValue(fullPath, out var item))",
            method,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PastingIntoABoxTargetsTheSubTabTheBoxIsCurrentlyShowing()
    {
        var solutionDirectory = FindSolutionDirectory();
        var menuSource = File.ReadAllText(Path.Combine(
            solutionDirectory,
            "CrabDesk.Runtime",
            "DesktopBoxForm.ContextMenus.cs"));
        var methodStart = menuSource.IndexOf(
            "private async Task PasteIntoBoxAsync(DesktopBox box)",
            StringComparison.Ordinal);
        var methodEnd = menuSource.IndexOf(
            "private void ShowImportFailures(",
            Math.Max(0, methodStart),
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        var method = menuSource[methodStart..methodEnd];
        Assert.Contains(
            "_activeManualTabIds.GetValueOrDefault(box.Id)",
            method,
            StringComparison.Ordinal);

        var runtimeSource = File.ReadAllText(Path.Combine(
            solutionDirectory,
            "CrabDesk.Runtime",
            "CrabDeskRuntime.cs"));
        Assert.Contains(
            "public async Task<BoxPasteResult> PasteIntoBoxAsync(Guid boxId, Guid? targetTabId = null)",
            runtimeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "var manualTabId = ResolveManualTabTarget(box, targetTabId);",
            runtimeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "targetTabId: manualTabId);",
            runtimeSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "await ImportFilesAsync(external, boxId, clipboard.Move, manualTabId);",
            runtimeSource,
            StringComparison.Ordinal);
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
