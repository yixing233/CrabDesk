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
    public void MergesManualLabelsWithoutOverridingAiAssignments()
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

        Assert.Equal(["工作", "学习"], assignments.Select(item => item.Label));
    }
}
