using System.Xml.Linq;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class AiClassificationPageLayoutTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void AiClassificationPageUsesWorkbenchLayout()
    {
        var document = LoadAiClassificationPage();
        var root = Assert.IsType<XElement>(document.Root?.Elements().Single());
        Assert.Equal("Grid", root.Name.LocalName);

        var workspaceRepeater = document
            .Descendants(Presentation + "ItemsRepeater")
            .Single(element => (string?)element.Attribute("ItemsSource") == "{Binding WorkspaceItems}");
        Assert.Equal("ScrollViewer", workspaceRepeater.Parent?.Name.LocalName);

        var activity = document
            .Descendants(Presentation + "TabViewItem")
            .Single(element => (string?)element.Attribute("Header") == "AI 活动");
        Assert.Contains(activity.Ancestors(Presentation + "TabView"),
            element => (string?)element.Attribute("Grid.Column") == "1");
    }

    [Fact]
    public void AiClassificationPageUsesItsFullHeightForTheWorkingArea()
    {
        var document = LoadAiClassificationPage();
        var root = Assert.IsType<XElement>(document.Root?.Elements().Single());

        Assert.Null(root.Element(Presentation + "Grid.RowDefinitions"));

        var workArea = root
            .Elements(Presentation + "Grid")
            .Single();
        Assert.Null((string?)workArea.Attribute("MinHeight"));
        Assert.True((double?)workArea.Attribute("MinWidth") >= 800d);

        Assert.DoesNotContain(
            document.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "AI 整理工作台");

        var activity = document
            .Descendants(Presentation + "TabViewItem")
            .Single(element => (string?)element.Attribute("Header") == "AI 活动");
        Assert.Contains(activity.Ancestors(Presentation + "TabView"),
            element => (string?)element.Attribute("Grid.Column") == "1");
    }

    [Fact]
    public void InspectorUsesFourHorizontalTabs()
    {
        var document = LoadAiClassificationPage();
        var inspector = document
            .Descendants(Presentation + "TabView")
            .Single(element => (string?)element.Attribute("Grid.Column") == "1");

        Assert.Equal(
            ["流程", "AI 活动", "分类设置", "模型设置"],
            inspector.Elements(Presentation + "TabViewItem")
                .Select(element => (string?)element.Attribute("Header")));

        var activity = inspector.Elements(Presentation + "TabViewItem")
            .Single(element => (string?)element.Attribute("Header") == "AI 活动");
        Assert.Single(activity.Descendants(Presentation + "Expander"));
    }

    [Fact]
    public void WorkspaceCardsKeepAFixedWidthDuringWindowResize()
    {
        var document = LoadAiClassificationPage();
        var workspaceRepeater = document
            .Descendants(Presentation + "ItemsRepeater")
            .Single(element => (string?)element.Attribute("ItemsSource") == "{Binding WorkspaceItems}");
        var layout = workspaceRepeater
            .Descendants(Presentation + "UniformGridLayout")
            .Single();
        var card = workspaceRepeater
            .Descendants(Presentation + "DataTemplate")
            .Single()
            .Elements(Presentation + "Border")
            .Single();

        Assert.Equal("None", (string?)layout.Attribute("ItemsStretch"));
        Assert.Equal("132", (string?)card.Attribute("Width"));
    }

    [Fact]
    public void AiActivityTabUsesOneAssistantMessageWithCollapsibleThinking()
    {
        var document = LoadAiClassificationPage();
        var activity = document
            .Descendants(Presentation + "TabViewItem")
            .Single(element => (string?)element.Attribute("Header") == "AI 活动");

        Assert.Empty(activity.Descendants(Presentation + "ListView"));
        var thinking = activity.Descendants(Presentation + "Expander").Single();
        Assert.Equal("{Binding IsThinkingExpanded, Mode=TwoWay}", (string?)thinking.Attribute("IsExpanded"));
        var outputPanels = activity
            .Descendants(Presentation + "TextBox")
            .Where(element => (string?)element.Attribute("Text") is "{Binding ReasoningOutput}" or "{Binding StructuredOutput}")
            .ToArray();

        Assert.Equal(2, outputPanels.Length);
        Assert.Contains(outputPanels, panel => panel.Ancestors(Presentation + "Expander").Any());
    }

    [Fact]
    public void AiActivityOutputPanelsAvoidExpensiveLongTextWrapping()
    {
        var document = LoadAiClassificationPage();
        var outputPanels = document
            .Descendants(Presentation + "TextBox")
            .Where(element => (string?)element.Attribute("Text") is "{Binding ReasoningOutput}" or "{Binding StructuredOutput}")
            .ToArray();

        Assert.Equal(2, outputPanels.Length);
        Assert.All(outputPanels, panel =>
        {
            Assert.Equal("NoWrap", (string?)panel.Attribute("TextWrapping"));
            Assert.Equal("Auto", (string?)panel.Attribute("ScrollViewer.HorizontalScrollBarVisibility"));
            Assert.Equal("Auto", (string?)panel.Attribute("ScrollViewer.VerticalScrollBarVisibility"));
        });
    }

    [Fact]
    public void AiActivityMessageShowsFiveUsageMetrics()
    {
        var document = LoadAiClassificationPage();
        var activity = document
            .Descendants(Presentation + "TabViewItem")
            .Single(element => (string?)element.Attribute("Header") == "AI 活动");
        var metricBindings = activity
            .Descendants(Presentation + "TextBlock")
            .Select(element => (string?)element.Attribute("Text"))
            .Where(value => value is "{Binding TotalDurationText}" or "{Binding FirstTokenLatencyText}" or
                "{Binding InputTokenText}" or "{Binding OutputTokenText}" or "{Binding TotalTokenText}")
            .ToArray();

        Assert.Equal(5, metricBindings.Length);
    }

    private static XDocument LoadAiClassificationPage()
    {
        return XDocument.Load(Path.Combine(
            FindSolutionDirectory(),
            "CrabDesk.WinUI",
            "Views",
            "AiClassificationPage.xaml"));
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
