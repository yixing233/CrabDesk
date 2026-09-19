using CrabDesk.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CrabDesk.WinUI.ViewModels;

public sealed class AiClassificationActivityViewModel : ObservableObject
{
    private AiClassificationActivity _activity;

    public AiClassificationActivityViewModel(AiClassificationActivity activity) => _activity = activity;

    public string ItemKey => _activity.ItemKey;
    public string ItemName => _activity.ItemName;
    public AiClassificationActivityPhase Phase => _activity.Phase;
    public string DisplayText => _activity.DisplayText;
    public string StatusText => IsRunning ? "正在分析…" : DisplayText;
    public bool IsRunning => Phase == AiClassificationActivityPhase.Analyzing;
    public bool IsClassified => Phase == AiClassificationActivityPhase.Classified;
    public bool IsCompleted => Phase is AiClassificationActivityPhase.Classified or AiClassificationActivityPhase.Uncertain or AiClassificationActivityPhase.Failed or AiClassificationActivityPhase.Stopped;
    public bool IsUncertain => Phase is AiClassificationActivityPhase.Uncertain or AiClassificationActivityPhase.Failed;
    public bool IsStopped => Phase == AiClassificationActivityPhase.Stopped;

    public void Update(AiClassificationActivity activity)
    {
        if (_activity == activity) return;
        _activity = activity;
        OnPropertyChanged(string.Empty);
    }
}
