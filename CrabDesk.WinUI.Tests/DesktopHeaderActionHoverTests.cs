using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopHeaderActionHoverTests
{
    [Fact]
    public void MenuButtonTracksAndRendersItsHoverState()
    {
        var input = ReadRuntimeSource("DesktopBoxForm.Input.cs");
        var rendering = ReadRuntimeSource("DesktopBoxForm.Rendering.cs");
        var form = ReadRuntimeSource("DesktopBoxForm.cs");

        Assert.Contains("_hoveredMenuBoxId", form, StringComparison.Ordinal);
        Assert.Contains("var menuBoxId =", input, StringComparison.Ordinal);
        Assert.Contains("_hoveredMenuBoxId = menuBoxId", input, StringComparison.Ordinal);
        Assert.Contains("_hoveredMenuBoxId == geometry.Box.Id", rendering, StringComparison.Ordinal);
        Assert.Contains("private static void DrawMenuButton", rendering, StringComparison.Ordinal);
    }

    private static string ReadRuntimeSource(string fileName) => File.ReadAllText(Path.Combine(
        FindSolutionDirectory(),
        "CrabDesk.Runtime",
        fileName));

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
