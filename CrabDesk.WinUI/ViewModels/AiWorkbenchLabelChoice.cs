namespace CrabDesk.WinUI.ViewModels;

public enum AiWorkbenchLabelChoiceKind
{
    /// <summary>Put the item into the box named by <see cref="AiWorkbenchLabelChoice.Label"/>.</summary>
    Category,

    /// <summary>"不归类": leave the item on the desktop when the preview is applied.</summary>
    KeepOnDesktop,

    /// <summary>Drop the user's edit and go back to what the model suggested.</summary>
    RestoreAiSuggestion
}

/// <summary>
/// One entry of a workbench card's classification menu. The page turns these into
/// menu items; the view model interprets the chosen entry.
/// </summary>
public sealed record AiWorkbenchLabelChoice(
    AiWorkbenchItemViewModel Item,
    AiWorkbenchLabelChoiceKind Kind,
    string? Label,
    bool IsCurrent)
{
    public string Text => Kind switch
    {
        AiWorkbenchLabelChoiceKind.Category => Label ?? string.Empty,
        AiWorkbenchLabelChoiceKind.KeepOnDesktop => "不归类",
        _ => $"恢复 AI 建议：{Label}"
    };
}
