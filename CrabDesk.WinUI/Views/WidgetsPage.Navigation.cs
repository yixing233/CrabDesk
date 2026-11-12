using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CrabDesk.WinUI.Views;

public sealed partial class WidgetsPage
{
    private bool _appearanceOpen;

    private void WidgetAppearanceButton_Click(object sender, RoutedEventArgs args)
    {
        FlushEditorChanges();
        _appearanceOpen = true;
        LoadGlobalWidgetAppearance();
        UpdatePageNavigation();
    }

    private void WidgetPageBack_Click(object sender, RoutedEventArgs args)
    {
        if (_appearanceOpen)
        {
            _appearanceOpen = false;
            UpdatePageNavigation();
            RefreshEditorPreview();
        }
        else ShowOverview();
    }

    private void UpdatePageNavigation()
    {
        OverviewView.Visibility = Show(!_appearanceOpen && !_editing);
        EditorView.Visibility = Show(!_appearanceOpen && _editing);
        AppearanceView.Visibility = Show(_appearanceOpen);
        WidgetPageBack.Visibility = Show(_appearanceOpen || _editing);
        WidgetAppearanceButton.Visibility = Show(!_appearanceOpen);
        WidgetPageTitle.Text = _appearanceOpen ? "外观设置" : _editing ? _selected?.DisplayTitle ?? "组件设置" : "小组件";
        WidgetPageSubtitle.Text = _appearanceOpen ? "统一设置组件材质，更改自动保存。"
            : _editing ? "调整外观与内容，更改自动保存。" : "添加组件，让桌面更顺手。";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(WidgetPageBack,
            _appearanceOpen && _editing ? "返回组件编辑" : "返回小组件");
    }

    private void RefreshEditorPreview()
    {
        if (!_editing || _selected is null) return;
        EditorPreview.PreviewAppearance = PreviewAppearance();
        EditorPreview.Widget = _selected;
        EditorPreview.PreviewRevision++;
    }

    private void EditorView_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        var narrow = args.NewSize.Width < 720;
        EditorPreviewColumn.Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(280);
        Grid.SetColumnSpan(EditorPreviewPane, narrow ? 2 : 1);
        Grid.SetColumn(EditorScroll, narrow ? 0 : 1);
        Grid.SetColumnSpan(EditorScroll, narrow ? 2 : 1);
        Grid.SetRow(EditorScroll, narrow ? 1 : 0);
        Grid.SetRowSpan(EditorScroll, narrow ? 1 : 2);
        EditorPreviewPane.Padding = new Thickness(narrow ? 12 : 16);
        EditorPreviewTitle.Visibility = Show(!narrow);
        EditorPreviewMetadata.Visibility = Show(!narrow);
        EditorPreview.Height = narrow ? 140 : 230;
    }
}
