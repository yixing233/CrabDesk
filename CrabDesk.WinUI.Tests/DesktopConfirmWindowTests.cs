using CrabDesk.WinUI.Windows;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopConfirmWindowTests
{
    [Fact]
    public void DialogIsCentredOnTheOwnerWorkAreaAtTheOwnerDpi()
    {
        var bounds = DesktopConfirmWindow.CalculateBounds(0, 0, 2560, 1380, 120);

        Assert.Equal(550, bounds.Width);
        Assert.Equal(260, bounds.Height);
        Assert.Equal(1005, bounds.X);
        Assert.Equal(560, bounds.Y);
    }

    [Fact]
    public void ConfirmationWindowIsBuiltOnceAndHiddenBetweenRequests()
    {
        var window = ReadSource("CrabDesk.WinUI", "Windows", "DesktopConfirmWindow.xaml.cs");
        var app = ReadSource("CrabDesk.WinUI", "App.xaml.cs");

        // Creating a WinUI window on every "删除盒子" stalled the UI thread the
        // desktop surfaces share; the dialog is prewarmed and reused instead.
        Assert.Contains("AppWindow.Hide();", window, StringComparison.Ordinal);
        Assert.DoesNotContain("Close();", window, StringComparison.Ordinal);
        Assert.Contains("DesktopConfirmWindow.Prewarm", app, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] pathParts)
    {
        return File.ReadAllText(Path.Combine([FindSolutionDirectory(), .. pathParts]));
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
