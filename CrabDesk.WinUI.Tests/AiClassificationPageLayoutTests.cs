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

        var conversation = document
            .Descendants(Presentation + "ListView")
            .Single(element => (string?)element.Attribute("ItemsSource") == "{Binding Conversation}");
        Assert.Contains(conversation.Ancestors(Presentation + "Border"),
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

        var conversation = document
            .Descendants(Presentation + "ListView")
            .Single(element => (string?)element.Attribute("ItemsSource") == "{Binding Conversation}");
        Assert.Contains(conversation.Ancestors(Presentation + "Border"),
            element => (string?)element.Attribute("Grid.Column") == "1");
    }

    [Fact]
    public void RightPaneIsAConversationFlowWithSettingsInsteadOfTabs()
    {
        var document = LoadAiClassificationPage();
        var inspector = document
            .Descendants(Presentation + "Border")
            .Single(element => (string?)element.Attribute("Grid.Column") == "1" &&
                element.Descendants(Presentation + "ListView").Any());

        // The old four-tab inspector is replaced by a single conversation flow.
        Assert.Empty(document.Descendants(Presentation + "TabViewItem"));

        var conversation = inspector
            .Descendants(Presentation + "ListView")
            .Single(element => (string?)element.Attribute("ItemsSource") == "{Binding Conversation}");
        Assert.Equal("None", (string?)conversation.Attribute("SelectionMode"));

        var settingsButton = inspector
            .Descendants(Presentation + "Button")
            .Single(element => (string?)element.Attribute("Click") == "SettingsButton_OnClick");
        Assert.Equal("打开 AI 设置", (string?)settingsButton.Attribute("AutomationProperties.Name"));
    }

    [Fact]
    public void AssistantMessagesCarryThinkingAndToolCallAttachments()
    {
        var document = LoadAiClassificationPage();
        var conversation = document
            .Descendants(Presentation + "ListView")
            .Single(element => (string?)element.Attribute("ItemsSource") == "{Binding Conversation}");
        var template = conversation.Descendants(Presentation + "DataTemplate").First();

        var thinking = template
            .Descendants(Presentation + "Expander")
            .Single(element => (string?)element.Attribute("IsExpanded") == "{Binding IsThinkingExpanded, Mode=TwoWay}");
        Assert.NotNull(thinking.Descendants(Presentation + "TextBox")
            .Single(element => (string?)element.Attribute("Text") == "{Binding ThinkingText}"));

        var toolCalls = template
            .Descendants(Presentation + "ItemsControl")
            .Single(element => (string?)element.Attribute("ItemsSource") == "{Binding ToolCalls}");
        Assert.NotNull(toolCalls);
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
    public void ConversationMessagesNeverUseManualTabNavigation()
    {
        var document = LoadAiClassificationPage();
        var activity = document
            .Descendants(Presentation + "ListView")
            .Single(element => (string?)element.Attribute("ItemsSource") == "{Binding Conversation}");

        Assert.Empty(activity.Descendants(Presentation + "TabViewItem"));
    }

    [Fact]
    public void AiActivityOutputPanelsAvoidExpensiveLongTextWrapping()
    {
        var document = LoadAiClassificationPage();
        var outputPanels = document
            .Descendants(Presentation + "TextBox")
            .Where(element => (string?)element.Attribute("Text") is "{Binding ThinkingText}" or "{Binding ResultJsonText}")
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
        var conversation = document
            .Descendants(Presentation + "ListView")
            .Single(element => (string?)element.Attribute("ItemsSource") == "{Binding Conversation}");
        var metricBindings = conversation
            .Descendants(Presentation + "TextBlock")
            .Select(element => (string?)element.Attribute("Text"))
            .Where(value => value == "{Binding MetricsText}")
            .ToArray();

        Assert.Single(metricBindings);
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
