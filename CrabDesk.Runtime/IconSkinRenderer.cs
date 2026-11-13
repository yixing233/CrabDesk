using System.Drawing;
using CrabDesk.Native;

namespace CrabDesk.Runtime;

/// <summary>
/// The single place both icon surfaces turn an item's shell image into what they
/// draw. Keeping it here means the desktop layer and the box layer cannot end up
/// with different plates for the same item.
/// </summary>
internal static class IconSkinRenderer
{
    /// <summary>
    /// Returns the bitmap to draw for <paramref name="parsingName"/>, or null when
    /// the shell has no image for it. The caller owns the returned bitmap.
    /// </summary>
    internal static Bitmap? Load(
        ShellIconProvider? provider,
        string parsingName,
        int pixelSize,
        IconSkinKind kind,
        IconSkinOptions skin)
    {
        if (provider is null) return null;
        var includeShortcutOverlay = kind != IconSkinKind.Shortcut;
        var source = provider.GetIcon(parsingName, pixelSize, includeShortcutOverlay);
        if (source is null) return null;

        if (!skin.Enabled && kind != IconSkinKind.Shortcut)
        {
            return new Bitmap(source);
        }

        try
        {
            return FlatIconSkin.Render(source, pixelSize, kind, skin, parsingName);
        }
        catch (Exception)
        {
            // A skin failure must never cost the user an icon: fall back to the
            // plain shell image, which is exactly what they would see without it.
            return new Bitmap(source);
        }
    }
}
