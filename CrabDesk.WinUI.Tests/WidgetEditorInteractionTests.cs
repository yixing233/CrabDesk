using System.Xml.Linq;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class WidgetEditorInteractionTests
{
    [Fact]
    public void EditorHasASeparateDetailViewWithPreviewAndReturnNavigation()
    {
        var document = XDocument.Load(Path.Combine(SolutionDirectory(), "CrabDesk.WinUI", "Views", "WidgetsPage.xaml"));
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace controls = "using:CrabDesk.WinUI.Controls";
        var editor = document.Descendants(ui + "Grid").Single(element => (string?)element.Attribute(x + "Name") == "EditorView");
        var overview = document.Descendants(ui + "Grid").Single(element => (string?)element.Attribute(x + "Name") == "OverviewView");
        Assert.Same(overview.Parent, editor.Parent);
        Assert.Equal("Collapsed", (string?)editor.Attribute("Visibility"));
        Assert.Contains(editor.Descendants(controls + "WidgetPreview"), element => (string?)element.Attribute(x + "Name") == "EditorPreview");
        Assert.Contains(document.Descendants(ui + "Button"), element => (string?)element.Attribute(x + "Name") == "WidgetPageBack");
        Assert.Empty(document.Descendants(ui + "SplitView"));
    }

    [Fact]
    public void EditorUsesNativeScrollViewportAndRetainsKeyboardInput()
    {
        var document = XDocument.Load(Path.Combine(SolutionDirectory(), "CrabDesk.WinUI", "Views", "WidgetsPage.xaml"));
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var scroll = document.Descendants(ui + "ScrollViewer").Single(element => (string?)element.Attribute(x + "Name") == "EditorScroll");
        Assert.Null(scroll.Attribute("Style"));
        Assert.Equal("Disabled", (string?)scroll.Attribute("HorizontalScrollMode"));
        Assert.Equal("Enabled", (string?)scroll.Attribute("VerticalScrollMode"));
        Assert.Equal("False", (string?)scroll.Attribute("IsTabStop"));
        Assert.DoesNotContain(document.Descendants(ui + "ControlTemplate"), element => (string?)element.Attribute("TargetType") == "ScrollViewer");
        foreach (var id in new[] { "TitleInput", "SizePicker", "WidgetFontFamily", "NetworkDisplayModePicker", "FileShelfModePicker" })
            Assert.Contains(scroll.Descendants(), element => (string?)element.Attribute(x + "Name") == id);
    }

    private static string SolutionDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CrabDesk.sln"))) return directory.FullName;
        throw new InvalidOperationException("CrabDesk.sln not found.");
    }
}
