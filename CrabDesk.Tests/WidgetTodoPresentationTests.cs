using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class WidgetTodoPresentationTests
{
    [Fact]
    public void GroupingPreservesTheStoredOrderAndKeepsTheDraftBeforeTheCompletedSection()
    {
        var finished = new WidgetTodoItem { Text = "已完成", IsCompleted = true };
        var first = new WidgetTodoItem { Text = "第一件" };
        var second = new WidgetTodoItem { Text = "第二件", EndDate = new(2026, 10, 31) };
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Todo, Tasks = [finished, first, second] };
        var collapsed = WidgetTodoPresentation.Rows(widget);
        Assert.Equal(new[] { first, second, null }, collapsed.Select(row => row.Task));
        Assert.True(collapsed[^1].IsCompletedHeader);
        var expanded = WidgetTodoPresentation.Rows(widget, completedExpanded: true, adding: true);
        Assert.Equal(new[] { first, second, null, null, finished }, expanded.Select(row => row.Task));
        Assert.False(expanded[2].IsCompletedHeader); Assert.True(expanded[3].IsCompletedHeader);
        Assert.Equal(new[] { finished, first, second }, widget.Tasks);
        Assert.Equal(new[] { 30, 46, 30, 28, 30 }, expanded.Select(row => row.Height));
    }

    [Fact]
    public void CompletedSectionIsAbsentUntilThereAreCompletedTasks()
    {
        var task = new WidgetTodoItem { Text = "任务" };
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Todo, Tasks = [task] };
        Assert.Same(task, Assert.Single(WidgetTodoPresentation.Rows(widget)).Task);
        task.IsCompleted = true;
        Assert.True(Assert.Single(WidgetTodoPresentation.Rows(widget)).IsCompletedHeader);
        task.IsCompleted = false;
        Assert.Same(task, Assert.Single(WidgetTodoPresentation.Rows(widget)).Task);
        widget.Tasks.Clear();
        Assert.Empty(WidgetTodoPresentation.Rows(widget));
    }

    [Theory]
    [InlineData("2026-10-29", "2026-10-31", "10月29日—31日")]
    [InlineData("2026-10-31", "2026-11-02", "10月31日—11月2日")]
    [InlineData("2026-10-30", "2026-10-30", "10月30日")]
    [InlineData("2027-10-29", "2027-10-31", "2027年10月29日—31日")]
    [InlineData("2027-10-31", "2027-11-02", "2027年10月31日—11月2日")]
    [InlineData("2026-12-31", "2027-01-01", "12月31日—2027年1月1日")]
    [InlineData(null, "2026-10-31", "截止 10月31日")]
    [InlineData("2027-01-01", null, "开始 2027年1月1日")]
    [InlineData(null, null, "")]
    public void CompactDatesRetainTheMeaningOfRangesAndYears(string? start, string? end, string expected)
    {
        var task = new WidgetTodoItem { StartDate = start is null ? null : DateOnly.Parse(start), EndDate = end is null ? null : DateOnly.Parse(end) };
        Assert.Equal(expected, WidgetTodoPresentation.DateLabel(task, 2026));
        Assert.Equal(start is null ? null : DateOnly.Parse(start), task.StartDate);
        Assert.Equal(end is null ? null : DateOnly.Parse(end), task.EndDate);
    }
}
