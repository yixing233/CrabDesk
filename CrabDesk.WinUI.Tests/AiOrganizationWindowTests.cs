using System.Xml.Linq;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class AiOrganizationWindowTests
{
    private const string PresentationNamespace =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const string XamlNamespace =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly XNamespace Presentation = PresentationNamespace;
    private static readonly XNamespace Xaml = XamlNamespace;

    [Fact]
    public void OrganizationPageExposesTheAiWorkbenchEntry()
    {
        var document = LoadXaml("CrabDesk.WinUI", "Views", "OrganizationPage.xaml");
        var entry = document
            .Descendants(Presentation + "Button")
            .Single(element => (string?)element.Attribute("Click") == "OpenAiOrganization_OnClick");
        var label = entry
            .Descendants(Presentation + "TextBlock")
            .Single(element => (string?)element.Attribute("Text") == "打开 AI 整理");

        Assert.NotNull(label);
    }

    [Fact]
    public void AiOrganizationWindowHostsTheWorkbenchPage()
    {
        var document = LoadXaml("CrabDesk.WinUI", "Windows", "AiOrganizationWindow.xaml");
        var host = document
            .Descendants(Presentation + "ContentControl")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "WorkbenchHost");

        Assert.NotNull(host);
        // ContentControl aligns its content Left/Top by default, which sizes the workbench page
        // to whole icon columns and leaves the remainder as a blank strip at the window's right edge.
        Assert.Equal("Stretch", (string?)host.Attribute("HorizontalContentAlignment"));
        Assert.Equal("Stretch", (string?)host.Attribute("VerticalContentAlignment"));
    }

    [Fact]
    public void AiOrganizationWindowConfiguresItsIconAndMinimumSize()
    {
        var source = ReadSource("CrabDesk.WinUI", "Windows", "AiOrganizationWindow.xaml.cs");
        var project = LoadXaml("CrabDesk.WinUI", "CrabDesk.WinUI.csproj");

        Assert.Contains("WindowIcon.Apply(this);", source, StringComparison.Ordinal);
        Assert.Contains("InstallMinimumSizeTracking();", source, StringComparison.Ordinal);
        Assert.Contains("MinimumWidthDips", source, StringComparison.Ordinal);
        Assert.Contains(project.Descendants("Content"), element =>
            (string?)element.Attribute("Include") == "Assets\\CrabDesk.ico" &&
            (string?)element.Attribute("CopyToOutputDirectory") == "PreserveNewest");
    }

    [Fact]
    public void SettingsWindowAppliesTheSharedAppIcon()
    {
        // WinUI windows do not inherit the exe's ApplicationIcon: until MainWindow applied the
        // icon itself, the taskbar and Alt+Tab showed the generic window glyph for "CrabDesk 设置".
        var helper = ReadSource("CrabDesk.WinUI", "Windows", "WindowIcon.cs");
        var mainWindow = ReadSource("CrabDesk.WinUI", "MainWindow.xaml.cs");

        Assert.Contains("AppWindow.SetIcon(", helper, StringComparison.Ordinal);
        Assert.Contains("WindowIcon.Apply(this);", mainWindow, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] pathParts)
    {
        return File.ReadAllText(Path.Combine([FindSolutionDirectory(), .. pathParts]));
    }

    private static XDocument LoadXaml(params string[] pathParts)
    {
        return XDocument.Load(Path.Combine([FindSolutionDirectory(), .. pathParts]));
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
