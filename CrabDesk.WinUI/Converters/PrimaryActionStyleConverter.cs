using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace CrabDesk.WinUI.Converters;

/// <summary>
/// Resolves the accent button style while the bound flag is true and the default
/// style otherwise, so a pair of actions can swap which one reads as primary. Pass
/// "invert" as the parameter for the button that is primary while the flag is false.
/// </summary>
public sealed class PrimaryActionStyleConverter : IValueConverter
{
    public const string PrimaryStyleKey = "AccentButtonStyle";
    public const string SecondaryStyleKey = "DefaultButtonStyle";

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = GetStyleKey(value is true, parameter as string);
        return Application.Current?.Resources is { } resources &&
               resources.TryGetValue(key, out var style) &&
               style is Style
            ? style
            : DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

    public static string GetStyleKey(bool isPrimary, string? parameter)
    {
        if (string.Equals(parameter, "invert", StringComparison.OrdinalIgnoreCase))
        {
            isPrimary = !isPrimary;
        }

        return isPrimary ? PrimaryStyleKey : SecondaryStyleKey;
    }
}
