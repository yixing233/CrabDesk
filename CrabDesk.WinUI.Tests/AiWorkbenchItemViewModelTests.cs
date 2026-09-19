using CrabDesk.WinUI.ViewModels;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class AiWorkbenchItemViewModelTests
{
    [Fact]
    public void ManualLabelWinsOverTheAiSuggestion()
    {
        var item = new AiWorkbenchItemViewModel { ItemKey = "one", DisplayName = "One", HasPreview = true, AiLabel = "工作" };
        Assert.True(item.ShowsAiLabel);
        Assert.Equal("工作", item.EffectiveLabel);

        item.ManualLabel = "学习";

        Assert.Equal("学习", item.EffectiveLabel);
        Assert.True(item.IsManuallyLabeled);
        Assert.False(item.ShowsAiLabel);
        Assert.False(item.IsUncertain);
        Assert.True(item.HasAiLabel);
        Assert.Equal("学习", item.ClassificationText);
    }

    [Fact]
    public void ExcludedItemHasNoEffectiveLabelButKeepsTheSuggestionForRestore()
    {
        var item = new AiWorkbenchItemViewModel { ItemKey = "one", DisplayName = "One", HasPreview = true, AiLabel = "工作" };

        item.IsExcluded = true;

        Assert.Null(item.EffectiveLabel);
        Assert.False(item.IsUncertain);
        Assert.False(item.ShowsAiLabel);
        Assert.False(item.IsManuallyLabeled);
        Assert.True(item.HasAiLabel);
        Assert.Equal("不归类", item.ClassificationText);

        item.IsExcluded = false;

        Assert.Equal("工作", item.EffectiveLabel);
        Assert.True(item.ShowsAiLabel);
    }

    [Fact]
    public void UncertainOnlyWhileAPreviewedItemHasNoLabelAtAll()
    {
        var item = new AiWorkbenchItemViewModel { ItemKey = "one", DisplayName = "One" };
        Assert.False(item.IsUncertain);
        Assert.Equal(string.Empty, item.ClassificationText);

        item.HasPreview = true;
        Assert.True(item.IsUncertain);
        Assert.Equal("待确认", item.ClassificationText);

        item.ManualLabel = "学习";
        Assert.False(item.IsUncertain);
        Assert.True(item.IsManuallyLabeled);
        Assert.Equal("学习", item.EffectiveLabel);
    }

    [Fact]
    public void ResetClearsEveryReviewDecision()
    {
        var item = new AiWorkbenchItemViewModel
        {
            ItemKey = "one",
            DisplayName = "One",
            HasPreview = true,
            AiLabel = "工作",
            ManualLabel = "学习",
            IsExcluded = true
        };

        item.ResetClassification();

        Assert.False(item.HasPreview);
        Assert.Null(item.AiLabel);
        Assert.Null(item.ManualLabel);
        Assert.False(item.IsExcluded);
        Assert.Null(item.EffectiveLabel);
        Assert.Equal(string.Empty, item.ClassificationText);
    }

    [Fact]
    public void RaisesTheDerivedChipPropertiesWhenADecisionChanges()
    {
        var item = new AiWorkbenchItemViewModel { ItemKey = "one", DisplayName = "One", HasPreview = true, AiLabel = "工作" };
        var raised = new List<string?>();
        item.PropertyChanged += (_, eventArgs) => raised.Add(eventArgs.PropertyName);

        item.IsExcluded = true;

        Assert.Contains(nameof(AiWorkbenchItemViewModel.IsExcluded), raised);
        Assert.Contains(nameof(AiWorkbenchItemViewModel.EffectiveLabel), raised);
        Assert.Contains(nameof(AiWorkbenchItemViewModel.ShowsAiLabel), raised);
        Assert.Contains(nameof(AiWorkbenchItemViewModel.IsUncertain), raised);
        Assert.Contains(nameof(AiWorkbenchItemViewModel.ClassificationToolTip), raised);
    }
}
