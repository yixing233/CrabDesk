using CrabDesk.Core;
using CrabDesk.WinUI.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace CrabDesk.WinUI.Views;

public sealed class WidgetGalleryItem : ObservableObject
{
    public DesktopWidget Widget { get; }
    public bool IsCatalog { get; }
    public string IconAssetName => WidgetIconCatalog.GetName(Widget.Kind);
    private WidgetPreviewAppearance _appearance;
    public WidgetPreviewAppearance Appearance => _appearance;
    private string _caption;
    public string Caption => _caption;
    private string _title;
    public string Title => _title;
    private int _existingCount;
    public int ExistingCount => _existingCount;
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (SetProperty(ref _isSelected, value)) OnPropertyChanged(nameof(SelectionVisibility)); }
    }
    public int PreviewRevision { get; private set; }

    public WidgetGalleryItem(DesktopWidget widget, WidgetPreviewAppearance appearance, bool isCatalog, string caption, int existingCount = 0)
    {
        Widget = widget;
        _appearance = appearance;
        IsCatalog = isCatalog;
        _caption = caption;
        _title = widget.DisplayTitle;
        _existingCount = existingCount;
    }

    public void Refresh(WidgetPreviewAppearance appearance, string caption, int existingCount, bool refreshPreview)
    {
        SetProperty(ref _appearance, appearance, nameof(Appearance));
        SetProperty(ref _caption, caption, nameof(Caption));
        if (SetProperty(ref _title, Widget.DisplayTitle, nameof(Title))) OnPropertyChanged(nameof(AccessibleName));
        if (SetProperty(ref _existingCount, existingCount, nameof(ExistingCount)))
        {
            OnPropertyChanged(nameof(IsAdded));
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(ActionLabel));
            OnPropertyChanged(nameof(ActionIcon));
            OnPropertyChanged(nameof(AccessibleName));
        }
        if (refreshPreview)
        {
            PreviewRevision++;
            OnPropertyChanged(nameof(PreviewRevision));
        }
    }

    public bool IsAdded => !IsCatalog || ExistingCount > 0;
    public string StatusLabel => IsCatalog && ExistingCount > 1 ? $"已添加 {ExistingCount} 个" : IsAdded ? "已添加" : "未添加";
    public Visibility StatusVisibility => Visibility.Visible;
    public Visibility SelectionVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;
    public string ActionLabel => !IsCatalog ? "编辑组件" : IsAdded ? ExistingCount > 1 ? "查看已添加组件" : "编辑已有组件" : "添加到桌面";
    public string AccessibleName => (IsCatalog && ExistingCount > 1 ? "查看已添加的" : IsAdded ? "编辑已添加的" : "添加") + Title;
    public LucideIconName ActionIcon => !IsCatalog ? LucideIconName.ArrowRight : IsAdded ? LucideIconName.Check : LucideIconName.Plus;

    public bool Matches(string? query, bool? isAdded)
    {
        if (isAdded.HasValue && IsAdded != isAdded.Value) return false;
        var text = query?.Trim() ?? string.Empty;
        return text.Length == 0 || new[] { Title, DesktopWidgets.GetName(Widget.Kind), Description(Widget.Kind), Caption, Widget.Kind.ToString() }
            .Any(value => value.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    public static string Description(DesktopWidgetKind kind) => kind switch
    {
        DesktopWidgetKind.Clock => "模拟表盘与可选秒针",
        DesktopWidgetKind.Calendar => "浏览月份，标记今天",
        DesktopWidgetKind.Note => "随手记下灵感",
        DesktopWidgetKind.Todo => "记录并完成每一件事",
        DesktopWidgetKind.Translation => "随时翻译一段文字",
        DesktopWidgetKind.Weather => "关心城市的阴晴冷暖",
        DesktopWidgetKind.Performance => "实时查看 CPU、GPU、内存与磁盘",
        DesktopWidgetKind.ControlCenter => "开关、音量、亮度与媒体控制",
        DesktopWidgetKind.Lyrics => "随音乐滚动，逐字点亮歌词",
        DesktopWidgetKind.FileShelf => "通过引用或独立副本收集文件",
        _ => "进程流量排行、实时网速与曲线"
    };
}
