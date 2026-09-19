namespace CrabDesk.Core;

public enum AiClassificationActivityPhase
{
    Analyzing,
    Classified,
    Uncertain,
    Failed,
    Stopped
}

public sealed record AiClassificationActivity(
    string ItemKey,
    string ItemName,
    AiClassificationActivityPhase Phase,
    string? Label = null)
{
    public string DisplayText => Phase switch
    {
        AiClassificationActivityPhase.Analyzing => $"正在分析 · {ItemName}",
        AiClassificationActivityPhase.Classified when !string.IsNullOrWhiteSpace(Label) => $"{ItemName}  →  {Label}",
        AiClassificationActivityPhase.Failed => $"{ItemName} · 分析失败",
        AiClassificationActivityPhase.Stopped => $"{ItemName} · 已停止分析",
        _ => $"{ItemName} · 待确认"
    };
}
