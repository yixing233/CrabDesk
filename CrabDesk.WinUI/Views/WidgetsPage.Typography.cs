using CrabDesk.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CrabDesk.WinUI.Views;

public sealed partial class WidgetsPage
{
    private void LoadWidgetTypography(DesktopWidget widget)
    {
        TypographySettings.Visibility = Show(widget.Kind != DesktopWidgetKind.Lyrics);
        if (widget.Kind == DesktopWidgetKind.Lyrics) return;
        var families = App.GetService<Services.IFontCatalogService>().FontFamilies.Append(widget.FontFamily)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        WidgetFontFamily.ItemsSource = families;
        WidgetFontFamily.SelectedItem = families.First(name => string.Equals(name, widget.FontFamily, StringComparison.CurrentCultureIgnoreCase));
        WidgetFontScale.Value = widget.FontScale * 100;
        WidgetFontWeight.ItemsSource = new[] { "默认（保留文字层级）", "常规", "粗体" };
        WidgetFontWeight.SelectedIndex = widget.FontWeight switch { 400 => 1, 700 => 2, _ => 0 };
        WidgetFontItalic.IsOn = widget.FontItalic;
    }

    private void SaveWidgetTypography()
    {
        if (_loading || _selected is not { } widget || widget.Kind == DesktopWidgetKind.Lyrics ||
            WidgetFontFamily.SelectedItem is not string family || WidgetFontWeight.SelectedIndex < 0 || !double.IsFinite(WidgetFontScale.Value)) return;
        Save(target =>
        {
            target.FontFamily = WidgetTypography.NormalizeFamily(family);
            target.FontScale = WidgetTypography.NormalizeScale(WidgetFontScale.Value / 100);
            target.FontWeight = WidgetFontWeight.SelectedIndex switch { 1 => 400, 2 => 700, _ => 0 };
            target.FontItalic = WidgetFontItalic.IsOn;
        });
    }

    private void WidgetTypography_SelectionChanged(object sender, SelectionChangedEventArgs args) => SaveWidgetTypography();
    private void WidgetFontScale_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => SaveWidgetTypography();
    private void WidgetFontItalic_Toggled(object sender, RoutedEventArgs args) => SaveWidgetTypography();

    private void ResetWidgetTypography_Click(object sender, RoutedEventArgs args)
    {
        Save(widget => { widget.FontFamily = BoxAppearance.DefaultFontFamily; widget.FontScale = 1; widget.FontWeight = 0; widget.FontItalic = false; });
        if (_selected is not { } selected) return;
        _loading = true;
        try { LoadWidgetTypography(selected); }
        finally { _loading = false; }
    }
}
