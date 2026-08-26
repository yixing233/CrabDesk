using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopConfirmWindowTests
{
    [Fact]
    public void WindowIsSizedAndPositionedBeforeItBecomesVisible()
    {
        var source = File.ReadAllText(Path.Combine(
            FindSolutionDirectory(),
            "CrabDesk.WinUI",
            "Windows",
            "DesktopConfirmWindow.xaml.cs"));
        var showMethod = source.IndexOf(
            "internal static Task<bool> ShowAsync(",
            StringComparison.Ordinal);
        var configure = source.IndexOf("window.ConfigureWindow();", showMethod, StringComparison.Ordinal);
        var activate = source.IndexOf("window.Activate();", showMethod, StringComparison.Ordinal);

        Assert.True(showMethod >= 0);
        Assert.True(configure > showMethod);
        Assert.True(activate > configure);
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
