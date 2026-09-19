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
        var root = Assert.IsType<XElement>(document.Root?.Elements()
            .Single(element => element.Name.LocalName == "Grid"));
        Assert.Equal("Grid", root.Name.LocalName);

        var workspaceRepeater = document
            .Descendants(Presentation + "ItemsRepeater")
            .Single(element => (string?)element.Attribute("ItemsSource") == "{Binding VisibleWorkspaceItems}");
        Assert.Equal("ScrollViewer", workspaceRepeater.Parent?.Name.LocalName);

        var conversation = document
            .Descendants(Presentation + "ListView")
            .Single(element => (string?)element.Attribute("ItemsSource") == "{Binding Conversation}");
        Assert.Contains(conversation.Ancestors(Presentation + "Border"),
            element => (string?)element.Attribute("Grid.Column") == "1");
    }

    [Fact]
    public void WorkspacePaneHasALiveSearchBoxAboveTheGrid()
    {
        var document = LoadAiClassificationPage();
        var search = document.Descendants(Presentation + "AutoSuggestBox").Single();
        var pane = Assert.IsType<XElement>(search.Parent);
        var gridRow = pane
            .Elements(Presentation + "Grid")
            .Single(element => element
                .Descendants(Presentation + "ItemsRepeater")
                .Any(repeater => (string?)repeater.Attribute("ItemsSource") == "{Binding VisibleWorkspaceItems}"));

        Assert.Equal("Find", (string?)search.Attribute("QueryIcon"));
        Assert.Equal("WorkspaceSearchBox_OnTextChanged", (string?)search.Attribute("TextChanged"));
        Assert.StartsWith("{Binding WorkspaceFilter, Mode=TwoWay", (string?)search.Attribute("Text"));
        // The search row sits between the title row and the grid row of the same pane.
        Assert.True(int.Parse((string)search.Attribute("Grid.Row")!) < int.Parse((string)gridRow.Attribute("Grid.Row")!));

        Assert.Contains(gridRow.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "{Binding NoFilterMatchesText}");
    }

    [Fact]
    public void AiClassificationPageUsesItsFullHeightForTheWorkingArea()
    {
        var document = LoadAiClassificationPage();
        var root = Assert.IsType<XElement>(document.Root?.Elements()
            .Single(element => element.Name.LocalName == "Grid"));

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
    public void WorkspaceCardsKeepAFixedWidthWhileTheirColumnsFillTheAvailableWidth()
    {
        var document = LoadAiClassificationPage();
        var workspaceRepeater = document
            .Descendants(Presentation + "ItemsRepeater")
            .Single(element => (string?)element.Attribute("ItemsSource") == "{Binding VisibleWorkspaceItems}");
        var layout = workspaceRepeater
            .Descendants(Presentation + "UniformGridLayout")
            .Single();
        var card = workspaceRepeater
            .Descendants(Presentation + "DataTemplate")
            .Single()
            .Elements(Presentation + "Border")
            .Single();

        // The card never deforms with the window: it has a fixed width and is centered in its slot.
        Assert.Equal("132", (string?)card.Attribute("Width"));
        Assert.Equal("Center", (string?)card.Attribute("HorizontalAlignment"));
        // The slots, however, stretch so whole columns always span the row; with "None" the
        // leftover after the last whole column piled up as a blank strip on the right.
        Assert.Equal("Fill", (string?)layout.Attribute("ItemsStretch"));
        Assert.Equal("Horizontal", (string?)layout.Attribute("Orientation"));
        Assert.Equal("Start", (string?)layout.Attribute("ItemsJustification"));
    }

    [Fact]
    public void WorkspaceCardsReserveFixedRowsSoClassificationLabelsNeverClip()
    {
        var document = LoadAiClassificationPage();
        var workspaceRepeater = document
            .Descendants(Presentation + "ItemsRepeater")
            .Single(element => (string?)element.Attribute("ItemsSource") == "{Binding VisibleWorkspaceItems}");
        var layout = workspaceRepeater
            .Descendants(Presentation + "UniformGridLayout")
            .Single();
        var card = workspaceRepeater
            .Descendants(Presentation + "DataTemplate")
            .Single()
            .Elements(Presentation + "Border")
            .Single();

        // The card is exactly the uniform slot, so nothing added after a run can overflow
        // and be clipped by the card's rounded corners (the old MinHeight card grew past its slot).
        Assert.Null(card.Attribute("MinHeight"));
        Assert.Equal((string?)layout.Attribute("MinItemWidth"), (string?)card.Attribute("Width"));
        Assert.Equal((string?)layout.Attribute("MinItemHeight"), (string?)card.Attribute("Height"));

        // The classification chip has a reserved fixed-height row of its own.
        var labelRow = card
            .Descendants(Presentation + "Grid")
            .Single(element => (string?)element.Attribute("Grid.Row") == "2");
        Assert.NotNull(labelRow.Attribute("Height"));
        var chip = labelRow.Elements(Presentation + "Button").Single();
        Assert.Equal((string?)labelRow.Attribute("Height"), (string?)chip.Attribute("Height"));
        Assert.Equal(
            "{Binding HasPreview, Converter={StaticResource BooleanToVisibilityConverter}}",
            (string?)chip.Attribute("Visibility"));
        Assert.Empty(card.Descendants(Presentation + "ComboBox"));
    }

    [Fact]
    public void WorkspaceCardChipIsAMenuButtonThatReflectsTheReviewState()
    {
        var document = LoadAiClassificationPage();
        var chip = document
            .Descendants(Presentation + "ItemsRepeater")
            .Single(element => (string?)element.Attribute("ItemsSource") == "{Binding VisibleWorkspaceItems}")
            .Descendants(Presentation + "Button")
            .Single(element => (string?)element.Attribute("Click") == "ClassificationChip_OnClick");

        // Editing only makes sense for an item that will be applied.
        Assert.Equal("{Binding IsSelected}", (string?)chip.Attribute("IsEnabled"));
        Assert.Equal("{Binding ClassificationToolTip}", (string?)chip.Attribute("ToolTipService.ToolTip"));
        Assert.Single(chip.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "{Binding ClassificationText}");

        // One pill fill and one leading icon per review state: AI suggestion, manual label, excluded, uncertain.
        string[] states = ["ShowsAiLabel", "IsManuallyLabeled", "IsExcluded", "IsUncertain"];
        foreach (var state in states)
        {
            var visibility = $"{{Binding {state}, Converter={{StaticResource BooleanToVisibilityConverter}}}}";
            var fill = Assert.Single(chip.Descendants(Presentation + "Border"),
                element => (string?)element.Attribute("Visibility") == visibility);
            Assert.NotNull(fill.Attribute("Background"));
            Assert.Single(chip.Descendants().Where(element => element.Name.LocalName == "LucideIcon"),
                element => (string?)element.Attribute("Visibility") == visibility);
        }

        // The trailing chevron tells the user the chip opens a menu.
        Assert.Contains(chip.Descendants().Where(element => element.Name.LocalName == "LucideIcon"),
            element => (string?)element.Attribute("Icon") == "ChevronDown");
    }

    [Fact]
    public void ComposerSwapsThePrimaryActionOnceAPreviewExists()
    {
        var document = LoadAiClassificationPage();
        var buttons = document.Descendants(Presentation + "Button").ToArray();

        var classify = Assert.Single(buttons, element => (string?)element.Attribute("Command") == "{Binding ClassifyCommand}");
        var apply = Assert.Single(buttons, element => (string?)element.Attribute("Command") == "{Binding ApplyCommand}");
        var cancel = Assert.Single(buttons, element => (string?)element.Attribute("Command") == "{Binding CancelCommand}");

        // The run button re-labels itself ("开始 AI 分类" → "重新分类") and hands the accent to "确认应用".
        Assert.Contains(classify.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "{Binding ClassifyButtonText}");
        Assert.Equal(
            "{Binding HasPreview, Converter={StaticResource PrimaryActionStyleConverter}, ConverterParameter=invert}",
            (string?)classify.Attribute("Style"));
        Assert.Equal(
            "{Binding HasPreview, Converter={StaticResource PrimaryActionStyleConverter}}",
            (string?)apply.Attribute("Style"));
        // Stop only exists while a run is in progress.
        Assert.Equal(
            "{Binding IsBusy, Converter={StaticResource BooleanToVisibilityConverter}}",
            (string?)cancel.Attribute("Visibility"));
    }

    [Fact]
    public void WorkspaceCardCheckboxSitsInTheCornerBesideTheIconInsteadOfOnTopOfIt()
    {
        var document = LoadAiClassificationPage();
        var card = document
            .Descendants(Presentation + "ItemsRepeater")
            .Single(element => (string?)element.Attribute("ItemsSource") == "{Binding VisibleWorkspaceItems}")
            .Descendants(Presentation + "DataTemplate")
            .Single()
            .Elements(Presentation + "Border")
            .Single();

        var checkBox = card.Descendants(Presentation + "CheckBox").Single();
        Assert.Equal("{Binding IsSelected, Mode=TwoWay}", (string?)checkBox.Attribute("IsChecked"));
        Assert.Equal("Right", (string?)checkBox.Attribute("HorizontalAlignment"));
        Assert.Equal("Top", (string?)checkBox.Attribute("VerticalAlignment"));
        // Compact box only (the default CheckBox reserves 120px for a text label).
        Assert.Equal("20", (string?)checkBox.Attribute("Width"));
        Assert.Equal("20", (string?)checkBox.Attribute("MinWidth"));
        Assert.Equal("0", (string?)checkBox.Attribute("Padding"));

        // The icon button is centered in its own row rather than sharing the checkbox's cell.
        var iconButton = card
            .Descendants(Presentation + "Button")
            .Single(element => (string?)element.Attribute("CommandParameter") == "{Binding}");
        Assert.Equal("Center", (string?)iconButton.Attribute("HorizontalAlignment"));
        Assert.NotSame(checkBox.Parent, iconButton.Parent);

        // Deselected cards dim their content; the checkbox is outside the dimmed subtree.
        var dimmed = card
            .Descendants(Presentation + "Grid")
            .Single(element => (string?)element.Attribute("Opacity") ==
                "{Binding IsSelected, Converter={StaticResource BooleanToOpacityConverter}}");
        Assert.Contains(iconButton, dimmed.Descendants(Presentation + "Button"));
        Assert.DoesNotContain(checkBox, dimmed.Descendants(Presentation + "CheckBox"));
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

    [Fact]
    public void LiveTurnShowsARecentWindowWhileTheFinishedTurnExposesTheFullTrace()
    {
        var document = LoadAiClassificationPage();
        var conversation = document
            .Descendants(Presentation + "ListView")
            .Single(element => (string?)element.Attribute("ItemsSource") == "{Binding Conversation}");
        var itemsControls = conversation.Descendants(Presentation + "ItemsControl").ToArray();

        var live = Assert.Single(itemsControls,
            element => (string?)element.Attribute("ItemsSource") == "{Binding RecentClassificationActivities}");
        var full = Assert.Single(itemsControls,
            element => (string?)element.Attribute("ItemsSource") == "{Binding ClassificationActivities}");

        Assert.Equal("{StaticResource AiActivityRowTemplate}", (string?)live.Attribute("ItemTemplate"));
        Assert.Equal("{StaticResource AiActivityRowTemplate}", (string?)full.Attribute("ItemTemplate"));

        // The compact window is only for the running turn; the finished turn's toggle reveals every row.
        Assert.Contains(live.Ancestors(Presentation + "StackPanel"), element =>
            (string?)element.Attribute("Visibility") == "{Binding IsRunning, Converter={StaticResource BooleanToVisibilityConverter}}");
        Assert.Contains(full.Ancestors(Presentation + "StackPanel"), element =>
            (string?)element.Attribute("Visibility") == "{Binding IsRunning, Converter={StaticResource BooleanNegationToVisibilityConverter}}");
        // The full trace is toggled as a unit and capped to about the live window's height,
        // scrolling inside instead of stretching a 65-item message to 65 rows.
        var traceViewport = Assert.IsType<XElement>(full.Parent);
        Assert.Equal("ScrollViewer", traceViewport.Name.LocalName);
        Assert.Equal(
            "{Binding IsProcessExpanded, Converter={StaticResource BooleanToVisibilityConverter}}",
            (string?)traceViewport.Attribute("Visibility"));
        Assert.True((double?)traceViewport.Attribute("MaxHeight") is > 0 and <= 160);
        Assert.Equal("Auto", (string?)traceViewport.Attribute("VerticalScrollBarVisibility"));
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
