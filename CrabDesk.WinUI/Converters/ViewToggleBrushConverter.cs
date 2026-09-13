using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace CrabDesk.WinUI.Converters;

/// <summary>True renders the accent fill (active view toggle); false the quiet fill.</summary>
public sealed class ViewToggleBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isActive = value is true;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
        {
            isActive = !isActive;
        }

        var resources = Application.Current?.Resources;
        var active = resources?["AccentFillColorDefaultBrush"] as Brush;
        var quiet = resources?["ControlFillColorSecondaryBrush"] as Brush;
        return isActive ? active ?? quiet : quiet;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>White on the active accent toggle, primary text color otherwise.</summary>
public sealed class ViewToggleForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isActive = value is true;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
        {
            isActive = !isActive;
        }

        var resources = Application.Current?.Resources;
        if (isActive)
        {
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
        }
        return resources?["TextFillColorPrimaryBrush"] as Microsoft.UI.Xaml.Media.Brush
            ?? Microsoft.UI.Xaml.Application.Current?.Resources["TextFillColorPrimaryBrush"];
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
