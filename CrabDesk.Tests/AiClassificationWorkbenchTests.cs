using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class AiClassificationWorkbenchTests
{
    [Fact]
    public void SelectsOnlyKnownWorkspaceItemsInWorkspaceOrder()
    {
        var workspace = new AiClassificationWorkspace(12,
        [
            new AiClassificationWorkspaceItem("one", "One", DesktopItemKind.File, "C:\\One.txt"),
            new AiClassificationWorkspaceItem("two", "Two", DesktopItemKind.Shortcut, "C:\\Two.lnk")
        ]);

        var selected = AiClassificationWorkbench.Select(workspace, ["two", "missing", "two"]);

        Assert.Equal(["two"], selected.Select(item => item.ItemKey));
    }

    [Fact]
    public void MergesManualLabelsAndIgnoresUnknownLabelsOrKeys()
    {
        var preview = new AiClassificationPreview(4, 2,
            [new AiClassificationAssignment("one", "One", "工作")], [])
        {
            RequestedItemKeys = ["one", "two"]
        };

        var assignments = AiClassificationWorkbench.MergeManualAssignments(
            preview,
            new Dictionary<string, string>
            {
                ["two"] = "学习",
                ["one"] = "游戏",
                ["outside"] = "工作"
            },
            ["工作", "学习"]);

        // "游戏" is not an allowed label, so "one" keeps the AI suggestion; "outside" is out of scope.
        Assert.Equal([("one", "工作"), ("two", "学习")], assignments.Select(item => (item.ItemKey, item.Label)));
    }

    [Fact]
    public void ManualLabelsOverrideAiSuggestionsAndExcludedItemsStayOnTheDesktop()
    {
        var preview = new AiClassificationPreview(4, 4,
            [
                new AiClassificationAssignment("one", "One", "工作"),
                new AiClassificationAssignment("two", "Two", "工作"),
                new AiClassificationAssignment("three", "Three", "工作")
            ], [])
        {
            RequestedItemKeys = ["one", "two", "three", "four"]
        };

        var assignments = AiClassificationWorkbench.MergeManualAssignments(
            preview,
            new Dictionary<string, string>
            {
                ["one"] = " 学习 ",
                ["four"] = "学习"
            },
            ["工作", "学习"],
            excludedItemKeys: ["TWO", "four", " "]);

        // one: overridden (label trimmed, item name kept); two: excluded despite the AI label;
        // three: untouched; four: excluded, so its manual label is ignored too.
        Assert.Equal(
            [("one", "One", "学习"), ("three", "Three", "工作")],
            assignments.Select(item => (item.ItemKey, item.ItemName, item.Label)));
    }
}
