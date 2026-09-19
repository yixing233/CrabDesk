using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CrabDesk.Core;

namespace CrabDesk.WinUI.ViewModels;

public enum AiConversationRole
{
    System,
    User,
    Assistant
}

/// <summary>
/// One bubble in the AI workbench conversation flow. Assistant messages own
/// their streaming snapshots (thinking text, tool calls, metrics) so finished
/// turns freeze instead of following the latest run.
/// </summary>
public partial class AiConversationMessageViewModel : ObservableObject
{
    /// <summary>How many recently updated rows the live view keeps on screen.</summary>
    public const int RecentActivityWindow = 6;

    private string _text;
    private string _transportText = string.Empty;
    private string _thinkingText = string.Empty;
    private string _thinkingStatsText = string.Empty;
    private bool _isThinkingExpanded;
    private string _parsedText = string.Empty;
    private string _metricsText = string.Empty;
    private string _resultJsonText = string.Empty;
    private bool _isRunning;
    private bool _isWebSearchActive;
    private bool _isError;
    private int _classificationActivityTotal;
    private readonly HashSet<string> _finishedClassificationKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AiClassificationActivityViewModel> _activitiesByKey = new(StringComparer.OrdinalIgnoreCase);
    private bool _isProcessExpanded = true;
    private string _currentActivityText = string.Empty;
    private string _outcomeText = string.Empty;

    public ObservableCollection<AiConversationResultGroupViewModel> ResultGroups { get; } = [];
    public bool HasResultGroups => ResultGroups.Count > 0;
    public string OutcomeText { get => _outcomeText; private set => SetProperty(ref _outcomeText, value); }
    public bool IsProcessExpanded { get => _isProcessExpanded; set => SetProperty(ref _isProcessExpanded, value); }
    public string CurrentActivityText { get => _currentActivityText; private set => SetProperty(ref _currentActivityText, value); }
    public double ActivityProgressValue => _classificationActivityTotal == 0 ? 0 : (double)_finishedClassificationKeys.Count / _classificationActivityTotal * 100;

    public AiConversationMessageViewModel(AiConversationRole role, string text)
    {
        Role = role;
        _text = text;
        TimeText = DateTime.Now.ToString("HH:mm:ss");
        ToolCalls.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasToolCalls));
        ClassificationActivities.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasClassificationActivities));
            OnPropertyChanged(nameof(ClassificationActivitySummary));
        };
    }

    public AiConversationRole Role { get; }
    public string TimeText { get; }

    public bool IsSystem => Role == AiConversationRole.System;
    public bool IsUser => Role == AiConversationRole.User;
    public bool IsAssistant => Role == AiConversationRole.Assistant;

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    public string TransportText
    {
        get => _transportText;
        set => SetProperty(ref _transportText, value);
    }

    public string ThinkingText
    {
        get => _thinkingText;
        set => SetProperty(ref _thinkingText, value);
    }

    public string ThinkingStatsText
    {
        get => _thinkingStatsText;
        set => SetProperty(ref _thinkingStatsText, value);
    }

    public bool IsThinkingExpanded
    {
        get => _isThinkingExpanded;
        set => SetProperty(ref _isThinkingExpanded, value);
    }

    public string ParsedText
    {
        get => _parsedText;
        set => SetProperty(ref _parsedText, value);
    }

    public string MetricsText
    {
        get => _metricsText;
        set => SetProperty(ref _metricsText, value);
    }

    public string ResultJsonText
    {
        get => _resultJsonText;
        set => SetProperty(ref _resultJsonText, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        set => SetProperty(ref _isRunning, value);
    }

    public bool IsWebSearchActive
    {
        get => _isWebSearchActive;
        set => SetProperty(ref _isWebSearchActive, value);
    }

    public bool IsError
    {
        get => _isError;
        set => SetProperty(ref _isError, value);
    }

    public ObservableCollection<AiToolCallViewModel> ToolCalls { get; } = [];

    /// <summary>
    /// Every item this turn has touched, in first-seen order. Never trimmed: it backs
    /// the expandable "过程轨迹" once the turn finishes, so a 64-item run can be reviewed
    /// row by row after the fact.
    /// </summary>
    public ObservableCollection<AiClassificationActivityViewModel> ClassificationActivities { get; } = [];

    /// <summary>
    /// Sliding window over the rows updated most recently (oldest first). Backs the
    /// compact live view while the turn runs, so the feed stays short without
    /// discarding history from <see cref="ClassificationActivities"/>.
    /// </summary>
    public ObservableCollection<AiClassificationActivityViewModel> RecentClassificationActivities { get; } = [];

    public bool HasToolCalls => ToolCalls.Count > 0;
    public bool HasClassificationActivities => ClassificationActivities.Count > 0;

    public void SetClassificationActivityTotal(int total)
    {
        _classificationActivityTotal = Math.Max(0, total);
        OnPropertyChanged(nameof(ClassificationActivitySummary));
        OnPropertyChanged(nameof(ActivityProgressValue));
    }

    public void UpdateActivity(AiClassificationActivity activity)
    {
        if (!_activitiesByKey.TryGetValue(activity.ItemKey, out var entry))
        {
            if (!IsRunning)
            {
                return;
            }
            entry = new AiClassificationActivityViewModel(activity);
            _activitiesByKey[activity.ItemKey] = entry;
            ClassificationActivities.Add(entry);
        }
        else
        {
            entry.Update(activity);
        }
        TouchRecentActivity(entry);
        if (activity.Phase is not AiClassificationActivityPhase.Analyzing)
        {
            _finishedClassificationKeys.Add(activity.ItemKey);
        }
        CurrentActivityText = activity.Phase == AiClassificationActivityPhase.Analyzing ? activity.DisplayText : string.Empty;
        OnPropertyChanged(nameof(ClassificationActivitySummary));
        OnPropertyChanged(nameof(ActivityProgressValue));
    }

    /// <summary>
    /// Moves <paramref name="entry"/> to the tail of the recent window and drops the
    /// oldest rows beyond <see cref="RecentActivityWindow"/>. Remove + Add rather than
    /// Move keeps the change notifications to the two actions every ItemsControl handles.
    /// </summary>
    private void TouchRecentActivity(AiClassificationActivityViewModel entry)
    {
        RecentClassificationActivities.Remove(entry);
        RecentClassificationActivities.Add(entry);
        while (RecentClassificationActivities.Count > RecentActivityWindow)
        {
            RecentClassificationActivities.RemoveAt(0);
        }
    }

    public void Complete(string outcome, bool isError)
    {
        Text = outcome;
        IsError = isError;
        foreach (var pending in ClassificationActivities.Where(item => item.IsRunning).ToArray())
        {
            pending.Update(new AiClassificationActivity(pending.ItemKey, pending.ItemName, AiClassificationActivityPhase.Stopped));
            _finishedClassificationKeys.Add(pending.ItemKey);
        }
        IsRunning = false;
        IsProcessExpanded = false;
        CurrentActivityText = string.Empty;
        OnPropertyChanged(nameof(ShowSuccessMark));
    }

    public void SetResultGroups(IEnumerable<AiClassificationGroupViewModel> groups, string outcome)
    {
        ResultGroups.Clear();
        foreach (var group in groups) ResultGroups.Add(new AiConversationResultGroupViewModel(group));
        OutcomeText = outcome;
        OnPropertyChanged(nameof(HasResultGroups));
        OnPropertyChanged(nameof(ShowSuccessMark));
    }

    public void MarkClassificationActivityFinished(string itemKey)
    {
        _finishedClassificationKeys.Add(itemKey);
        OnPropertyChanged(nameof(ClassificationActivitySummary));
    }

    public void RefreshClassificationActivitySummary() => OnPropertyChanged(nameof(ClassificationActivitySummary));

    /// <summary>Whether the finished turn shows the compact success mark row.</summary>
    public bool ShowSuccessMark => !IsRunning && !IsError && HasResultGroups;

    [RelayCommand]
    private void ToggleProcess() => IsProcessExpanded = !IsProcessExpanded;

    public string ClassificationActivitySummary
    {
        get
        {
            var finished = _finishedClassificationKeys.Count;
            var running = ClassificationActivities.FirstOrDefault(item => item.IsRunning);
            var total = _classificationActivityTotal > 0 ? _classificationActivityTotal : ClassificationActivities.Count;
            return running is not null
                ? $"正在处理 {finished + 1}/{total} · {running.ItemName}"
                : $"已处理 {finished}/{total} 项";
        }
    }
}
