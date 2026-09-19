using System.Drawing;

namespace CrabDesk.Runtime;

/// <summary>
/// The box outline drawn when "显示外边框" is enabled. The stroke is derived
/// from the box's own background instead of a fixed gray so a custom
/// background keeps a rim that reads as the same surface: it shifts the
/// background toward white on a dark box and toward black on a light one.
/// The width, color and opacity are all user-configurable.
/// </summary>
public static class BoxBorderStyle
{
    /// <summary>Sentinel color meaning "derive from each box's own background".</summary>
    public const string AutoColor = "Auto";

    public const double MinWidth = 1;
    public const double MaxWidth = 6;
    public const double MinOpacityPercent = 0;
    public const double MaxOpacityPercent = 100;

    /// <summary>Fallback color used when a configured value cannot be parsed.</summary>
    public static Color FallbackColor { get; } = Color.FromArgb(255, 255, 255);

    /// <summary>Dark target for a light box. A dark box uses white instead.</summary>
    public static Color DarkTarget { get; } = Color.FromArgb(18, 22, 28);

    /// <summary>
    /// How far the auto stroke moves the background toward its contrast target.
    /// A 1 DIP stroke is spread across two pixels by antialiasing, roughly
    /// halving its peak, so the auto rim needs a decisive shift to stay legible
    /// once the opacity slider is pulled down.
    /// </summary>
    public const float ContrastAmount = 0.5f;

    /// <summary>Clamps a configured stroke width into the supported range.</summary>
    public static float ResolveWidth(double configuredWidth) =>
        (float)Math.Clamp(configuredWidth, MinWidth, MaxWidth);

    /// <summary>Clamps a configured opacity percentage into 0-100.</summary>
    public static double ResolveOpacityPercent(double configuredOpacity) =>
        Math.Clamp(configuredOpacity, MinOpacityPercent, MaxOpacityPercent);

    /// <summary>Whether the configured color asks for the derived stroke.</summary>
    public static bool IsAuto(string? configuredColor) =>
        string.IsNullOrWhiteSpace(configuredColor) ||
        configuredColor.Trim().Equals(AutoColor, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the stroke color for a box background.
    /// <paramref name="isDarkSurface"/> selects the contrast direction and must
    /// be the same "dark box" signal the fill's text color uses, so the rim and
    /// the title always agree on which way is "lighter".
    /// </summary>
    public static Color ResolveStroke(
        Color boxBackground,
        bool isDarkSurface,
        string? configuredColor,
        double configuredOpacity)
    {
        var alpha = ResolveAlpha(configuredOpacity);
        if (!IsAuto(configuredColor) &&
            TryParseColor(configuredColor!, out var custom))
        {
            return Color.FromArgb(alpha, custom.R, custom.G, custom.B);
        }

        var target = isDarkSurface ? Color.White : DarkTarget;
        return Color.FromArgb(
            alpha,
            Shift(boxBackground.R, target.R),
            Shift(boxBackground.G, target.G),
            Shift(boxBackground.B, target.B));
    }

    /// <summary>Converts an opacity percentage into a 0-255 stroke alpha.</summary>
    public static int ResolveAlpha(double configuredOpacity) =>
        (int)Math.Round(ResolveOpacityPercent(configuredOpacity) / 100d * 255d);

    /// <summary>
    /// Scales the stroke by the box's resolved tint opacity. A faintly tinted
    /// box (low opacity, or acrylic) must not gain a solid outline, so the rim
    /// stays in proportion with the fill it sits on.
    /// </summary>
    public static Color ApplyTint(Color stroke, double tintOpacity) => Color.FromArgb(
        (int)Math.Round(stroke.A * Math.Clamp(tintOpacity, 0, 1)),
        stroke.R,
        stroke.G,
        stroke.B);

    /// <summary>
    /// Parses "#RRGGBB" or "#AARRGGBB" (with or without the leading '#').
    /// The alpha byte is ignored: stroke opacity is its own setting.
    /// </summary>
    public static bool TryParseColor(string value, out Color color)
    {
        color = FallbackColor;
        var hex = value.Trim().TrimStart('#');
        if (hex.Length is not (6 or 8))
        {
            return false;
        }
        var offset = hex.Length == 8 ? 2 : 0;
        try
        {
            color = Color.FromArgb(
                255,
                Convert.ToByte(hex.Substring(offset, 2), 16),
                Convert.ToByte(hex.Substring(offset + 2, 2), 16),
                Convert.ToByte(hex.Substring(offset + 4, 2), 16));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static int Shift(byte channel, byte target) =>
        (int)Math.Round(channel + (target - channel) * ContrastAmount);
}
