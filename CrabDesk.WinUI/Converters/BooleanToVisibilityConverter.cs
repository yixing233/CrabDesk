using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace CrabDesk.WinUI.Converters;

public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Visible;
}

public sealed class BooleanNegationConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is not true;
}

public sealed class BooleanNegationToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Collapsed;
}

public sealed class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Visible;
}

using Microsoft.UI.Xaml.Media;

/// <summary>True renders the accent fill (active view toggle); false the quiet fill.</summary>
public sealed class BoolToViewToggleBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var application = Microsoft.UI.Xaml.Application.Current;
        var active = application?.Resources["AccentFillColorDefaultBrush"] as Brush;
        var quiet = application?.Resources["ControlFillColorSecondaryBrush"] as Brush;
        var isActive = value is true;
        if (parameter as string == "invert")
        {
            isActive = !isActive;
        }
        return isActive ? active ?? quiet : quiet;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
