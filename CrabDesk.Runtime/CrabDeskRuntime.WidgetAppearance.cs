using CrabDesk.Core;

namespace CrabDesk.Runtime;

public sealed partial class CrabDeskRuntime
{
    public void SetWidgetCornerRadius(double value)
    {
        var normalized = double.IsFinite(value) ? Math.Clamp(value, 0, 20) : 8;
        if (State.Settings.Appearance.WidgetCornerRadius == normalized) return;
        State.Settings.Appearance.WidgetCornerRadius = normalized;
        NotifyWorkspaceChanged(true);
    }

    public void SetWidgetAcrylicFollowBox(bool value)
    {
        if (State.Settings.Appearance.WidgetAcrylic.FollowBoxAppearance == value) return;
        State.Settings.Appearance.WidgetAcrylic.FollowBoxAppearance = value;
        NotifyWorkspaceChanged(true);
    }

    public void SetWidgetAcrylicBackground(string value)
    {
        var normalized = WidgetAcrylicSettings.NormalizeBackground(value);
        if (normalized is null || State.Settings.Appearance.WidgetAcrylic.Background == normalized) return;
        State.Settings.Appearance.WidgetAcrylic.Background = normalized;
        NotifyTargetedWorkspaceChanged(manager => manager.RefreshBackdrop());
    }

    public void SetWidgetAcrylicOpacity(double value)
    {
        var normalized = WidgetAcrylicSettings.NormalizeOpacity(value);
        if (State.Settings.Appearance.WidgetAcrylic.BackgroundOpacity == normalized) return;
        State.Settings.Appearance.WidgetAcrylic.BackgroundOpacity = normalized;
        NotifyTargetedWorkspaceChanged(manager => manager.RefreshBackdrop());
    }

    public void SetWidgetAcrylicBlurStrength(double value)
    {
        var normalized = WidgetAcrylicSettings.NormalizeBlurStrength(value);
        if (State.Settings.Appearance.WidgetAcrylic.BlurStrength == normalized) return;
        State.Settings.Appearance.WidgetAcrylic.BlurStrength = normalized;
        NotifyTargetedWorkspaceChanged(manager => manager.RefreshBackdrop());
    }
}
