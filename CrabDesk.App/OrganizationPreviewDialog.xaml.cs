using System.Windows;
using CrabDesk.Core;

namespace CrabDesk.App;

public partial class OrganizationPreviewDialog : Window
{
    public OrganizationPreviewDialog(
        IReadOnlyList<OrganizationDecision> decisions,
        IReadOnlyList<DesktopBox> boxes,
        bool isDarkTheme)
    {
        InitializeComponent();
        var boxNames = boxes.ToDictionary(box => box.Id, box => box.Title);
        var rows = decisions.Select(decision => new PreviewRow(
            decision.ItemName,
            decision.RuleTitle,
            ResultText(decision, boxNames))).ToArray();
        PreviewList.ItemsSource = rows;
        SummaryText.Text = $"共 {rows.Length} 个桌面项目将按首条命中规则处理";
        SourceInitialized += (_, _) => ApplicationTheme.ApplyWindowChrome(this, isDarkTheme);
    }

    private void ApplyButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        DialogResult = true;
    }

    private static string ResultText(
        OrganizationDecision decision,
        IReadOnlyDictionary<Guid, string> boxNames) => decision.Action switch
    {
        OrganizationRuleAction.AssignToBox when decision.TargetBoxId is { } target && boxNames.TryGetValue(target, out var title)
            => $"移入 {title}",
        OrganizationRuleAction.AssignToBox => "目标盒子无效",
        OrganizationRuleAction.KeepUnassigned => "保持未分组",
        OrganizationRuleAction.Ignore => "忽略",
        _ => string.Empty
    };

    private sealed record PreviewRow(string ItemName, string RuleTitle, string Result);
}
