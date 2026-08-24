using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class AiClassificationProgressTests
{
    [Fact]
    public void ClassificationProgressCarriesOnlyAggregateBatchState()
    {
        var progress = new AiClassificationProgress(
            CompletedItems: 25,
            TotalItems: 100,
            CompletedBatches: 1,
            TotalBatches: 2,
            IsIndeterminate: true,
            Message: "正在请求 AI 分类");

        Assert.Equal(25, progress.CompletedItems);
        Assert.Equal(100, progress.TotalItems);
        Assert.Equal(1, progress.CompletedBatches);
        Assert.Equal(2, progress.TotalBatches);
        Assert.True(progress.IsIndeterminate);
        Assert.Equal("正在请求 AI 分类", progress.Message);
    }
}
