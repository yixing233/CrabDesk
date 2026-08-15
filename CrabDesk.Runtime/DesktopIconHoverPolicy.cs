namespace CrabDesk.Runtime;

/// <summary>
/// Gives an interactive desktop box priority over the icon layer below it.
/// </summary>
internal static class DesktopIconHoverPolicy
{
    internal static bool CanHoverDesktopIcon(bool pointerOverBox) => !pointerOverBox;
}
