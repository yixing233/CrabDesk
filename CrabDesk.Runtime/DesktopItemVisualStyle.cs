using System.Drawing;

namespace CrabDesk.Runtime;

/// <summary>
/// Shared hover/selection visuals for desktop icons (DesktopIconSurface) and
/// box items (DesktopBoxForm). Both renderers derive their item highlight
/// from this single definition so the two surfaces cannot drift apart: the
/// selection treatment is a translucent fill of the configured SelectionColor
/// and the hover treatment is the same color brightened by
/// <see cref="HoverBrightness"/> with a brighter fill and a border.
/// </summary>
public static class DesktopItemVisualStyle
{
    /// <summary>Fill opacity of the settled selection highlight.</summary>
    public const int SelectedFillAlpha = 112;

    /// <summary>Fill opacity of the hover highlight.</summary>
    public const int HoverFillAlpha = 156;

    /// <summary>Border opacity of the hover highlight.</summary>
    public const int HoverBorderAlpha = 232;

    /// <summary>How much the hover treatment brightens the selection color.</summary>
    public const float HoverBrightness = 0.30f;

    /// <summary>Extra space around the icon+label union that receives the highlight.</summary>
    public static float SelectionPadding(float iconSize) => Math.Max(1f, iconSize / 24f);

    /// <summary>Highlight corner radius, scaled with the icon size.</summary>
    public static float SelectionCornerRadius(float iconSize) => Math.Max(2f, iconSize / 12f);

    /// <summary>Blends <paramref name="color"/> towards white for the hover treatment.</summary>
    public static Color Brighten(Color color, float amount = HoverBrightness)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            color.A,
            (int)Math.Round(color.R + (255 - color.R) * amount),
            (int)Math.Round(color.G + (255 - color.G) * amount),
            (int)Math.Round(color.B + (255 - color.B) * amount));
    }
}
