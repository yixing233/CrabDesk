namespace CrabDesk.WinUI.ViewModels;

public sealed class AiConversationResultGroupViewModel
{
    public AiConversationResultGroupViewModel(AiClassificationGroupViewModel source)
    {
        CategoryName = source.CategoryName;
        IsUncertainGroup = source.IsUncertainGroup;
        var names = source.Items.Select(item => item.DisplayName).ToArray();
        ItemNames = names;
        ItemsText = string.Join("  ·  ", names);
        CountText = $"{names.Length} 项";
    }

    public string CategoryName { get; }
    public bool IsUncertainGroup { get; }
    public IReadOnlyList<string> ItemNames { get; }
    public string ItemsText { get; }
    public string CountText { get; }
}
