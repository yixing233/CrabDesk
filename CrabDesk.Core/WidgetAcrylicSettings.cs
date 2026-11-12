using System.Globalization;

namespace CrabDesk.Core;

public sealed class WidgetAcrylicSettings
{
    public const string DefaultBackground = "#FF20242C";
    public bool FollowBoxAppearance { get; set; } = true;
    public string Background { get; set; } = DefaultBackground;
    public double BackgroundOpacity { get; set; } = .45;
    public double BlurStrength { get; set; } = 50;

    public void Normalize()
    {
        Background = NormalizeBackground(Background) ?? DefaultBackground;
        BackgroundOpacity = NormalizeOpacity(BackgroundOpacity);
        BlurStrength = NormalizeBlurStrength(BlurStrength);
    }

    public static string? NormalizeBackground(string? value)
    {
        var hex = value?.Trim().TrimStart('#');
        if (hex?.Length is not (6 or 8) ||
            !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var color)) return null;
        return $"#FF{color & 0xFFFFFF:X6}";
    }

    public static double NormalizeOpacity(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : .45;
    public static double NormalizeBlurStrength(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 50;

    internal bool IsDark
    {
        get
        {
            var hex = NormalizeBackground(Background) ?? DefaultBackground;
            var color = uint.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return ((color >> 16) & 255) * .299 + ((color >> 8) & 255) * .587 + (color & 255) * .114 < 150;
        }
    }
}
