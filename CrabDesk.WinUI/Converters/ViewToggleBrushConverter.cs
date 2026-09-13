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
