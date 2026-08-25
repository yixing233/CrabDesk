using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace CrabDesk.WinUI.ViewModels;

/// <summary>
/// Transient presentation state for one desktop item in the AI workbench.
/// Nothing in this type is persisted or sent to the AI endpoint directly.
/// </summary>
public sealed class AiWorkbenchItemViewModel : ObservableObject
{
    private ImageSource? _icon;
    private bool _isSelected = true;
    private string? _aiLabel;
    private string? _manualLabel;
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

    public string? AiLabel
    {
        get => _aiLabel;
        set
        {
            if (!SetProperty(ref _aiLabel, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsUncertain));
            OnPropertyChanged(nameof(HasAiLabel));
            OnPropertyChanged(nameof(EffectiveLabel));
            OnPropertyChanged(nameof(ClassificationText));
        }
    }

    public string? ManualLabel
    {
        get => _manualLabel;
        set
        {
            if (SetProperty(ref _manualLabel, value))
            {
                OnPropertyChanged(nameof(EffectiveLabel));
                OnPropertyChanged(nameof(ClassificationText));
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
                OnPropertyChanged(nameof(IsUncertain));
                OnPropertyChanged(nameof(ClassificationText));
            }
        }
    }

    public bool IsUncertain => HasPreview && string.IsNullOrWhiteSpace(AiLabel);
    public bool HasAiLabel => !string.IsNullOrWhiteSpace(AiLabel);
    public bool HasIcon => Icon is not null;
    public string? EffectiveLabel => AiLabel ?? ManualLabel;
    public string ClassificationText => !string.IsNullOrWhiteSpace(AiLabel)
        ? AiLabel
        : !string.IsNullOrWhiteSpace(ManualLabel)
            ? ManualLabel
            : IsUncertain
                ? "待确认"
                : string.Empty;

    public void ResetClassification()
    {
        AiLabel = null;
        ManualLabel = null;
        HasPreview = false;
    }
}
