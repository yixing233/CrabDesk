using System.Globalization;
using Microsoft.UI.Xaml.Data;

namespace CrabDesk.WinUI.Converters;

/// <summary>
/// True renders the element fully opaque; false dims it (a deselected workbench
/// card, for example). Pass a number such as "0.3" as the converter parameter to
/// override the dimmed opacity.
/// </summary>
public sealed class BooleanToOpacityConverter : IValueConverter
{
    public const double DimmedOpacity = 0.45;

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is true)
        {
            return 1d;
        }

        return parameter is string text &&
               double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var dimmed)
            ? Math.Clamp(dimmed, 0d, 1d)
            : DimmedOpacity;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
