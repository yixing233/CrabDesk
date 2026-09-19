using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace CrabDesk.WinUI.ViewModels;

/// <summary>
/// Transient presentation state for one desktop item in the AI workbench.
/// Nothing in this type is persisted or sent to the AI endpoint directly.
/// </summary>
/// <remarks>
/// After a run the item carries the model's suggestion (<see cref="AiLabel"/>) plus
/// the user's review of it: a different category (<see cref="ManualLabel"/>) or
/// "不归类" (<see cref="IsExcluded"/>). <see cref="EffectiveLabel"/> is what "确认应用"
/// will act on: the user's decision wins over the suggestion.
/// </remarks>
public sealed class AiWorkbenchItemViewModel : ObservableObject
{
    private ImageSource? _icon;
    private bool _isSelected = true;
    private string? _aiLabel;
    private string? _manualLabel;
    private bool _isExcluded;
    private bool _hasPreview;

    public required string ItemKey { get; init; }
    public required string DisplayName { get; init; }

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (SetProperty(ref _icon, value))
            {
                OnPropertyChanged(nameof(HasIcon));
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>The category the model suggested, or null when it could not decide.</summary>
    public string? AiLabel
    {
        get => _aiLabel;
        set
        {
            if (SetProperty(ref _aiLabel, value))
            {
                OnPropertyChanged(nameof(HasAiLabel));
                NotifyClassificationChanged();
            }
        }
    }

    /// <summary>A category the user picked instead of (or in the absence of) the suggestion.</summary>
    public string? ManualLabel
    {
        get => _manualLabel;
        set
        {
            if (SetProperty(ref _manualLabel, value))
            {
                NotifyClassificationChanged();
            }
        }
    }

    /// <summary>"不归类": leave this item on the desktop when the preview is applied.</summary>
    public bool IsExcluded
    {
        get => _isExcluded;
        set
        {
            if (SetProperty(ref _isExcluded, value))
            {
                NotifyClassificationChanged();
            }
        }
    }

    public bool HasPreview
    {
        get => _hasPreview;
        set
        {
            if (SetProperty(ref _hasPreview, value))
            {
                NotifyClassificationChanged();
            }
        }
    }

    public bool HasIcon => Icon is not null;
    public bool HasAiLabel => !string.IsNullOrWhiteSpace(AiLabel);
    public bool IsManuallyLabeled => !IsExcluded && !string.IsNullOrWhiteSpace(ManualLabel);

    /// <summary>The category "确认应用" will use, or null to leave the item on the desktop.</summary>
    public string? EffectiveLabel => IsExcluded
        ? null
        : !string.IsNullOrWhiteSpace(ManualLabel) ? ManualLabel : AiLabel;

    /// <summary>Previewed, not excluded, and still without any category: the user has to decide.</summary>
    public bool IsUncertain => HasPreview && !IsExcluded && string.IsNullOrWhiteSpace(EffectiveLabel);

    /// <summary>The chip shows the untouched AI suggestion.</summary>
    public bool ShowsAiLabel => !IsExcluded && HasAiLabel && !IsManuallyLabeled;

    public string ClassificationText => IsExcluded
        ? "不归类"
        : !string.IsNullOrWhiteSpace(EffectiveLabel)
            ? EffectiveLabel
            : IsUncertain
                ? "待确认"
                : string.Empty;

    public string ClassificationToolTip => IsExcluded
        ? "已选择不归类，确认后保留在桌面。点击可更改"
        : IsManuallyLabeled
            ? $"手动指定：{ManualLabel}（AI 建议：{(HasAiLabel ? AiLabel : "无")}）。点击可更改"
            : ShowsAiLabel
                ? $"AI 建议：{AiLabel}。点击可更改或选择不归类"
                : IsUncertain
                    ? "AI 未能确定分类，点击选择标签或不归类"
                    : string.Empty;

    public void ResetClassification()
    {
        AiLabel = null;
        ManualLabel = null;
        IsExcluded = false;
        HasPreview = false;
    }

    private void NotifyClassificationChanged()
    {
        OnPropertyChanged(nameof(IsManuallyLabeled));
        OnPropertyChanged(nameof(EffectiveLabel));
        OnPropertyChanged(nameof(IsUncertain));
        OnPropertyChanged(nameof(ShowsAiLabel));
        OnPropertyChanged(nameof(ClassificationText));
        OnPropertyChanged(nameof(ClassificationToolTip));
    }
}
