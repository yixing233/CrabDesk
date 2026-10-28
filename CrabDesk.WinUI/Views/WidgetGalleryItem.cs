using CrabDesk.Core;
using CrabDesk.WinUI.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace CrabDesk.WinUI.Views;

public sealed class WidgetGalleryItem : ObservableObject
{
    public DesktopWidget Widget { get; }
    public bool IsCatalog { get; }
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

    public bool IsAdded => IsCatalog && ExistingCount > 0;
    public string StatusLabel => ExistingCount > 1 ? $"已添加 {ExistingCount} 个" : IsAdded ? "已添加" : "可添加";
    public Visibility StatusVisibility => IsCatalog ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SelectionVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;
    public string ActionLabel => IsAdded ? ExistingCount > 1 ? "查看已添加组件" : "编辑已有组件" : IsCatalog ? "添加到桌面" : "编辑组件";
    public string AccessibleName => (IsAdded ? ExistingCount > 1 ? "查看已添加的" : "编辑已添加的" : IsCatalog ? "添加" : "编辑") + Title;
    public LucideIconName ActionIcon => IsAdded ? LucideIconName.Check : IsCatalog ? LucideIconName.Plus : LucideIconName.ArrowRight;
}
