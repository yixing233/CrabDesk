using Xunit;

namespace CrabDesk.WinUI.Tests;

/// <summary>
/// The box header must always name the box, never the selected sub-tab.
///
/// A collapsed box draws no tab bar, so a header that borrowed the active
/// tab's title made a box wearing a sub-tab's name indistinguishable from a
/// real box of that name. In the reported case 组合盒子 was showing the tab
/// title 图片 while a genuine auto-generated 图片 box sat next to it, which
/// read as a duplicated box.
/// </summary>
public sealed class DesktopBoxHeaderTitleTests
{
    [Fact]
    public void HeaderDrawsTheBoxTitleRatherThanTheActiveSubTabTitle()
    {
        var rendering = ReadRuntimeSource("DesktopBoxForm.Rendering.cs");

        Assert.Contains("geometry.Box.Title,", rendering, StringComparison.Ordinal);
        Assert.DoesNotContain("displayedTitle", rendering, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "geometry.Box.ManualTabs.FirstOrDefault(tab => tab.Id == activeTabId)?.Title",
            rendering,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveSubTabIsStillMarkedByTheTabBar()
    {
        // Removing the header override must not remove the only indication of
        // which sub-tab is selected: the tab bar keeps its accent + underline.
        var rendering = ReadRuntimeSource("DesktopBoxForm.Rendering.cs");

        Assert.Contains(
            "geometry.ManualTabs[index].Id == geometry.ActiveManualTabId",
            rendering,
            StringComparison.Ordinal);
        Assert.Contains("using var underline = new Pen(accent, 2);", rendering, StringComparison.Ordinal);
    }

    private static string ReadRuntimeSource(string fileName) => File.ReadAllText(
        Path.Combine(FindSolutionDirectory(), "CrabDesk.Runtime", fileName));

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
