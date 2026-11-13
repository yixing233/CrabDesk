using CrabDesk.Core;
using CrabDesk.WinUI.Converters;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace CrabDesk.WinUI.Views;

public sealed partial class WidgetsPage
{
    private bool _loadingGlobalAppearance;

    private void LoadGlobalWidgetAppearance()
    {
        _loadingGlobalAppearance = true;
        try
        {
            WidgetCornerRadius.Value = _runtime.State.Settings.Appearance.ResolveWidgetCornerRadius();
            var settings = _runtime.State.Settings.Appearance.WidgetAcrylic;
            WidgetAcrylicFollowBox.IsOn = settings.FollowBoxAppearance;
            UpdateWidgetAcrylicSource();
            if (HexColor.TryParse(settings.Background, out var color))
            {
                WidgetAcrylicColor.Color = color;
                WidgetAcrylicSwatch.Background = new SolidColorBrush(color);
            }
            WidgetAcrylicColorLabel.Text = settings.Background;
            WidgetAcrylicOpacity.Value = settings.BackgroundOpacity * 100;
            WidgetAcrylicBlur.Value = settings.BlurStrength;
            UpdateGlobalWidgetAppearanceLabels();
        }
        finally { _loadingGlobalAppearance = false; }
    }

    private void UpdateGlobalWidgetAppearanceLabels()
    {
        WidgetCornerRadius.Header = $"圆角半径（{WidgetCornerRadius.Value:0} DIP）";
        WidgetAcrylicOpacity.Header = $"背景不透明度（{WidgetAcrylicOpacity.Value:0}%）";
        WidgetAcrylicBlur.Header = $"模糊强度（{WidgetAcrylicBlur.Value:0}%）";
    }

    private void WidgetCornerRadius_ValueChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_loadingGlobalAppearance || !IsLoaded) return;
        UpdateGlobalWidgetAppearanceLabels();
        _runtime.SetWidgetCornerRadius(args.NewValue);
    }

    private void UpdateWidgetAcrylicSource()
    {
        WidgetAcrylicCustomSettings.Visibility = Show(!WidgetAcrylicFollowBox.IsOn);
        WidgetAcrylicSourceHint.Text = WidgetAcrylicFollowBox.IsOn
            ? "背景色、强调色、不透明度与模糊效果统一跟随盒子。有盒子时使用第一个盒子的外观，没有盒子时使用默认盒子外观。"
            : "所有亚克力组件共用下方背景设置；强调色沿用盒子外观。独立设置会在切换来源后保留。";
    }

    private void WidgetAcrylicFollowBox_Toggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs args)
    {
        if (_loadingGlobalAppearance || !IsLoaded) return;
        UpdateWidgetAcrylicSource();
        _runtime.SetWidgetAcrylicFollowBox(WidgetAcrylicFollowBox.IsOn);
    }

    private void WidgetAcrylicColor_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_loadingGlobalAppearance || !IsLoaded) return;
        _runtime.SetWidgetAcrylicBackground(HexColor.Format(args.NewColor));
    }

    private void WidgetAcrylicOpacity_ValueChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_loadingGlobalAppearance || !IsLoaded) return;
        UpdateGlobalWidgetAppearanceLabels();
        _runtime.SetWidgetAcrylicOpacity(args.NewValue / 100);
    }

    private void WidgetAcrylicBlur_ValueChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_loadingGlobalAppearance || !IsLoaded) return;
        UpdateGlobalWidgetAppearanceLabels();
        _runtime.SetWidgetAcrylicBlurStrength(args.NewValue);
    }

    private void LoadWidgetAppearance(DesktopWidget widget)
    {
        WidgetAppearanceSettings.Visibility = Show(widget.Kind != DesktopWidgetKind.Lyrics && DesktopWidgets.HasBackground(widget));
        WidgetMaterialPicker.SelectedIndex = widget.BackgroundMaterial == WidgetBackgroundMaterial.Solid ? 0 : 1;
        WidgetThemePicker.SelectedIndex = (int)widget.ColorTheme;
        UpdateWidgetMaterialHint();
    }

    private void UpdateWidgetMaterialHint()
    {
        var inCenter = _selected?.Placement == WidgetPlacement.Center;
        WidgetThemePicker.Visibility = Show(!inCenter && WidgetMaterialPicker.SelectedIndex == 0);
        if (inCenter)
        {
            WidgetMaterialHint.Text = "配色跟随组件中心主题。";
            return;
        }
        WidgetMaterialHint.Text = WidgetMaterialPicker.SelectedIndex switch
        {
            0 => "黑白背景始终不透明，浅色为白底、深色为黑底；特色主题按组件类型搭配，图标与状态保留功能色。",
            _ => "在右上角“外观设置”中统一选择跟随盒子或使用组件背景设置。表盘、内容面板与功能配色保留组件自身设计。"
        };
    }

    private void WidgetMaterialPicker_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || WidgetMaterialPicker.SelectedIndex < 0) return;
        UpdateWidgetMaterialHint();
        Save(widget => widget.BackgroundMaterial = WidgetMaterialPicker.SelectedIndex == 0
            ? WidgetBackgroundMaterial.Solid : WidgetBackgroundMaterial.Acrylic);
    }

    private void WidgetThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || WidgetThemePicker.SelectedIndex < 0) return;
        Save(widget => widget.ColorTheme = (WidgetColorTheme)WidgetThemePicker.SelectedIndex);
    }
}
