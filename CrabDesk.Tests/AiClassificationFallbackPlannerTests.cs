using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class AiClassificationFallbackPlannerTests
{
    [Fact]
    public void SelectsOnlyUnclassifiedItemsWithinTheRunLimit()
    {
        var items = new[]
        {
            new AiClassificationInput("one", "已分类"),
            new AiClassificationInput("two", "待确认一"),
            new AiClassificationInput("three", "待确认二"),
            new AiClassificationInput("four", "待确认三")
        };
        var assignments = new[]
        {
            new AiClassificationAssignment("one", "已分类", "工作")
        };

        var result = AiClassificationFallbackPlanner.SelectForWebSearch(items, assignments, 2);

        Assert.Equal(["two", "three"], result.Select(item => item.ItemKey));
    }

    [Fact]
    public void ReturnsNoItemsWhenTheRemainingRunLimitIsZero()
    {
        var result = AiClassificationFallbackPlanner.SelectForWebSearch(
            [new AiClassificationInput("one", "待确认")],
            [],
            0);

        Assert.Empty(result);
    }
}
