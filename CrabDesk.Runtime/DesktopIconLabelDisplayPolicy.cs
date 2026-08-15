namespace CrabDesk.Runtime;

/// <summary>
/// Keeps the desktop label contract independent from transient pointer feedback.
/// </summary>
internal static class DesktopIconLabelDisplayPolicy
{
    internal static bool ShowsFullLabel(bool isSelected, bool isHovered) => isSelected;
}
