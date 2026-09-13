using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;

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

    public AiConversationMessageViewModel(AiConversationRole role, string text)
    {
        Role = role;
        _text = text;
        TimeText = DateTime.Now.ToString("HH:mm:ss");
        ToolCalls.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasToolCalls));
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

    public bool HasToolCalls => ToolCalls.Count > 0;
}
