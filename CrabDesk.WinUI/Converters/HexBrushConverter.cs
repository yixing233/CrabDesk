using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;

namespace CrabDesk.WinUI.Converters;

public sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        new SolidColorBrush(HexColor.TryParse(value as string, out var color) ? color : Colors.White);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is SolidColorBrush brush ? HexColor.Format(brush.Color) : "#FFFFFFFF";
}
