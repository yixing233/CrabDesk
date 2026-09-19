using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class AiClassificationActivityTests
{
    [Theory]
    [InlineData(AiClassificationActivityPhase.Analyzing, null, "正在分析 · Cockpit Tools")]
    [InlineData(AiClassificationActivityPhase.Classified, "专业工具", "Cockpit Tools  →  专业工具")]
    [InlineData(AiClassificationActivityPhase.Uncertain, null, "Cockpit Tools · 待确认")]
    [InlineData(AiClassificationActivityPhase.Failed, null, "Cockpit Tools · 分析失败")]
    public void ActivityProvidesConciseDisplayText(AiClassificationActivityPhase phase, string? label, string expected)
    {
        var activity = new AiClassificationActivity("key", "Cockpit Tools", phase, label);
        Assert.Equal(expected, activity.DisplayText);
    }

    [Fact]
    public void ClassifiedActivityWithBlankLabelFallsBackToUncertainText()
    {
        var activity = new AiClassificationActivity("key", "Cockpit Tools", AiClassificationActivityPhase.Classified, " ");
        Assert.Equal("Cockpit Tools · 待确认", activity.DisplayText);
    }
}
