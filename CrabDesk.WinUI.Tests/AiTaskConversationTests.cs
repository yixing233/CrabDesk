using CrabDesk.Core;
using CrabDesk.WinUI.ViewModels;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class AiTaskConversationTests
{
    [Fact]
    public void CompletedTaskCollapsesProcessAndCountsAllItems()
    {
        var message = new AiConversationMessageViewModel(AiConversationRole.Assistant, "开始") { IsRunning = true };
        message.SetClassificationActivityTotal(62);
        for (var i = 0; i < 62; i++)
            message.UpdateActivity(new($"key-{i}", $"图标 {i}", AiClassificationActivityPhase.Classified, "工具"));
        message.IsProcessExpanded = true;
        message.Complete("已完成", false);

        Assert.Equal("已处理 62/62 项", message.ClassificationActivitySummary);
        Assert.Equal(62, message.ClassificationActivities.Count);
        Assert.False(message.IsProcessExpanded);
        Assert.False(message.IsRunning);
        Assert.Equal(100d, message.ActivityProgressValue);
    }

    [Fact]
    public void FullTraceKeepsEveryItemWhileTheLiveViewShowsOnlyARecentWindow()
    {
        var message = new AiConversationMessageViewModel(AiConversationRole.Assistant, "开始") { IsRunning = true };
        message.SetClassificationActivityTotal(64);
        for (var i = 0; i < 64; i++)
        {
            message.UpdateActivity(new($"key-{i}", $"图标 {i}", AiClassificationActivityPhase.Analyzing));
            message.UpdateActivity(new($"key-{i}", $"图标 {i}", AiClassificationActivityPhase.Classified, "工具"));
        }
        message.Complete("已完成", false);

        // The expandable "过程轨迹" must let the user review all 64 rows, in the order they were first seen.
        Assert.Equal(
            Enumerable.Range(0, 64).Select(i => $"key-{i}"),
            message.ClassificationActivities.Select(item => item.ItemKey));
        Assert.All(message.ClassificationActivities, item => Assert.True(item.IsClassified));
        Assert.Equal("已处理 64/64 项", message.ClassificationActivitySummary);

        // The live view stays compact: only the most recently updated rows, oldest first.
        Assert.Equal(AiConversationMessageViewModel.RecentActivityWindow, message.RecentClassificationActivities.Count);
        Assert.Equal(
            Enumerable.Range(64 - AiConversationMessageViewModel.RecentActivityWindow, AiConversationMessageViewModel.RecentActivityWindow)
                .Select(i => $"key-{i}"),
            message.RecentClassificationActivities.Select(item => item.ItemKey));
    }

    [Fact]
    public void UpdatingARowMovesItToTheTailOfTheRecentWindowWithoutDuplicatingIt()
    {
        const int batch = AiConversationMessageViewModel.RecentActivityWindow + 2;
        var message = new AiConversationMessageViewModel(AiConversationRole.Assistant, "开始") { IsRunning = true };
        message.SetClassificationActivityTotal(batch);
        // The runtime marks a whole batch as analyzing before classifying it one item at a time.
        for (var i = 0; i < batch; i++)
            message.UpdateActivity(new($"key-{i}", $"图标 {i}", AiClassificationActivityPhase.Analyzing));
        Assert.Equal(
            Enumerable.Range(2, AiConversationMessageViewModel.RecentActivityWindow).Select(i => $"key-{i}"),
            message.RecentClassificationActivities.Select(item => item.ItemKey));

        // key-0 already scrolled out of the window; its classification brings it back at the tail.
        message.UpdateActivity(new("key-0", "图标 0", AiClassificationActivityPhase.Classified, "工具"));

        Assert.Equal("key-0", message.RecentClassificationActivities[^1].ItemKey);
        Assert.Single(message.RecentClassificationActivities, item => item.ItemKey == "key-0");
        Assert.Equal(AiConversationMessageViewModel.RecentActivityWindow, message.RecentClassificationActivities.Count);
        Assert.Equal(batch, message.ClassificationActivities.Count);
        // Both collections share the same row instance, so the full trace reflects the update too.
        Assert.Same(message.ClassificationActivities[0], message.RecentClassificationActivities[^1]);
        Assert.True(message.ClassificationActivities[0].IsClassified);
    }

    [Fact]
    public void CurrentActivityAndResultRetainItemIdentityWithoutDuplicates()
    {
        var message = new AiConversationMessageViewModel(AiConversationRole.Assistant, "开始") { IsRunning = true };
        message.SetClassificationActivityTotal(2);
        message.UpdateActivity(new("key", "Cockpit Tools", AiClassificationActivityPhase.Analyzing));
        Assert.Equal("正在分析 · Cockpit Tools", message.CurrentActivityText);
        message.UpdateActivity(new("key", "Cockpit Tools", AiClassificationActivityPhase.Classified, "专业工具"));
        message.UpdateActivity(new("key", "Cockpit Tools", AiClassificationActivityPhase.Classified, "专业工具"));
        Assert.Single(message.ClassificationActivities);
        Assert.Equal("已处理 1/2 项", message.ClassificationActivitySummary);
    }

    [Fact]
    public void CancellationStopsPendingRowsAndFreezesFinishedTurn()
    {
        var message = new AiConversationMessageViewModel(AiConversationRole.Assistant, "开始") { IsRunning = true };
        message.SetClassificationActivityTotal(2);
        message.UpdateActivity(new("key", "One", AiClassificationActivityPhase.Analyzing));
        message.Complete("已停止", false);
        message.UpdateActivity(new("late", "Two", AiClassificationActivityPhase.Classified, "工具"));
        Assert.Single(message.ClassificationActivities);
        Assert.False(message.ClassificationActivities[0].IsRunning);
        Assert.False(Assert.Single(message.RecentClassificationActivities).IsRunning);
        Assert.Equal("已处理 1/2 项", message.ClassificationActivitySummary);
    }

    [Fact]
    public void ResultGroupsAreSnapshotsOfNamesRatherThanLiveWorkspaceItems()
    {
        var group = new AiClassificationGroupViewModel { CategoryName = "工作" };
        group.Items.Add(new AiWorkbenchItemViewModel { ItemKey = "one", DisplayName = "One" });
        var message = new AiConversationMessageViewModel(AiConversationRole.Assistant, "完成");
        message.SetResultGroups([group], "已分类 1 项");
        group.Items.Clear();
        Assert.Equal("One", Assert.Single(message.ResultGroups).ItemsText);
        Assert.Equal("One", Assert.Single(message.ResultGroups[0].ItemNames));
        Assert.Equal("1 项", message.ResultGroups[0].CountText);
        Assert.True(message.HasResultGroups);
    }

    [Fact]
    public void ToggleProcessCommandExpandsAndCollapsesTheTrace()
    {
        var message = new AiConversationMessageViewModel(AiConversationRole.Assistant, "开始") { IsRunning = true };
        message.UpdateActivity(new("key", "One", AiClassificationActivityPhase.Analyzing));
        message.Complete("已完成", false);
        Assert.False(message.IsProcessExpanded);

        message.ToggleProcessCommand.Execute(null);
        Assert.True(message.IsProcessExpanded);

        message.ToggleProcessCommand.Execute(null);
        Assert.False(message.IsProcessExpanded);
    }

    [Fact]
    public void SuccessMarkAppearsOnlyForFinishedNonErrorTurnWithOutcome()
    {
        var message = new AiConversationMessageViewModel(AiConversationRole.Assistant, "开始") { IsRunning = true };
        Assert.False(message.ShowSuccessMark);

        message.Complete("已生成预览：3/3 项获得 AI 分类。", isError: false);
        // Completed without result groups (e.g. cancelled run): no success mark.
        Assert.False(message.ShowSuccessMark);

        var group = new AiClassificationGroupViewModel { CategoryName = "工作" };
        group.Items.Add(new AiWorkbenchItemViewModel { ItemKey = "one", DisplayName = "One" });
        message.SetResultGroups([group], "已分类 1 项");
        Assert.True(message.ShowSuccessMark);

        var failed = new AiConversationMessageViewModel(AiConversationRole.Assistant, "失败") { IsRunning = true };
        failed.Complete("请求失败", isError: true);
        Assert.False(failed.ShowSuccessMark);
    }
}
