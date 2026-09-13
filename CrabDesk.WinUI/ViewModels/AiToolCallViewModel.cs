using CommunityToolkit.Mvvm.ComponentModel;

namespace CrabDesk.WinUI.ViewModels;

/// <summary>One entry in the AI activity tool-call timeline.</summary>
public partial class AiToolCallViewModel : ObservableObject
{
    private string _detail = string.Empty;
    private bool _isRunning;
    private bool _isError;

    public string Icon { get; init; } = "Globe";
    public string Title { get; init; } = string.Empty;
    public string TimeText { get; init; } = string.Empty;

    public string Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        set => SetProperty(ref _isRunning, value);
    }

    public bool IsError
    {
        get => _isError;
        set => SetProperty(ref _isError, value);
    }
}
