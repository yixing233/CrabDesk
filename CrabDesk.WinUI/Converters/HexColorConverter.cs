using Microsoft.UI.Xaml.Data;
using Microsoft.UI;
using Windows.UI;

namespace CrabDesk.WinUI.Converters;

public sealed class HexColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        HexColor.TryParse(value as string, out var color) ? color : Colors.White;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Color color ? HexColor.Format(color) : "#FFFFFFFF";
}

internal static class HexColor
{
    public static bool TryParse(string? value, out Color color)
    {
        color = Colors.Transparent;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var hex = value.Trim().TrimStart('#');
        if (hex.Length is not (6 or 8) || !hex.All(Uri.IsHexDigit))
        {
            return false;
        }

        var offset = hex.Length == 8 ? 2 : 0;
        color = Color.FromArgb(
            hex.Length == 8 ? ParseByte(hex, 0) : byte.MaxValue,
            ParseByte(hex, offset),
            ParseByte(hex, offset + 2),
            ParseByte(hex, offset + 4));
        return true;
    }

    public static string Format(Color color) =>
        $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private static byte ParseByte(string value, int offset) =>
        System.Convert.ToByte(value.Substring(offset, 2), 16);
}
