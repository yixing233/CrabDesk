namespace CrabDesk.Core;

// Zero retains the appearance of widgets saved before individual materials.
public enum WidgetBackgroundMaterial { Inherit = 0, Solid = 1, Acrylic = 2 }
public enum WidgetColorTheme { Signature = 0, Light = 1, Dark = 2 }

public sealed record WidgetSurfaceAppearance(string Background, string Accent, string? GradientEnd, double Opacity, bool Acrylic);

public static class WidgetAppearance
{
    public static void ApplyNewDefaults(DesktopWidget widget)
    {
        widget.BackgroundMaterial = widget.Kind == DesktopWidgetKind.Lyrics
            ? WidgetBackgroundMaterial.Acrylic : WidgetBackgroundMaterial.Solid;
        widget.ColorTheme = WidgetColorTheme.Signature;
    }

    public static void Normalize(DesktopWidget widget)
    {
        if (!Enum.IsDefined(widget.BackgroundMaterial)) widget.BackgroundMaterial = WidgetBackgroundMaterial.Inherit;
        if (!Enum.IsDefined(widget.ColorTheme)) widget.ColorTheme = WidgetColorTheme.Signature;
        if (widget.Kind == DesktopWidgetKind.Lyrics) widget.BackgroundMaterial = WidgetBackgroundMaterial.Acrylic;
    }

    // Inherit is the saved legacy value for the same acrylic material.
    public static bool UsesAcrylic(DesktopWidget widget, bool globalAcrylic, WidgetAcrylicSettings? widgetAcrylic = null) =>
        DesktopWidgets.HasBackground(widget) &&
        (widget.Kind == DesktopWidgetKind.Lyrics || widget.BackgroundMaterial != WidgetBackgroundMaterial.Solid) &&
        (!(widgetAcrylic?.FollowBoxAppearance ?? true) || globalAcrylic);

    public static bool RequiresAcrylic(CrabDeskState state) => state.Settings.Appearance.UseAcrylicBoxes ||
        state.Widgets.Any(widget => widget.Placement == WidgetPlacement.Desktop &&
            UsesAcrylic(widget, false, state.Settings.Appearance.WidgetAcrylic));

    public static WidgetSurfaceAppearance Resolve(DesktopWidget widget, BoxAppearance inherited, bool acrylic,
        DateTimeOffset? now = null, WidgetAcrylicSettings? widgetAcrylic = null, bool forceSolid = false,
        WidgetColorTheme? colorTheme = null)
    {
        // Center hosts can override the surface without changing saved desktop materials or lyrics transparency.
        if (forceSolid || (widget.Kind != DesktopWidgetKind.Lyrics && widget.BackgroundMaterial == WidgetBackgroundMaterial.Solid))
        {
            var theme = colorTheme ?? widget.ColorTheme;
            var dark = theme == WidgetColorTheme.Dark ||
                theme == WidgetColorTheme.Signature &&
                widget.Kind is DesktopWidgetKind.Clock or DesktopWidgetKind.Weather or DesktopWidgetKind.Performance or
                    DesktopWidgetKind.Network or DesktopWidgetKind.ControlCenter;
            return new(dark ? widget.Kind == DesktopWidgetKind.Calendar ? "#FF000000" : "#FF1C1C1E" : "#FFFFFFFF",
                Accent(widget.Kind, dark), null, 1, false);
        }
        var material = widgetAcrylic ?? new WidgetAcrylicSettings();
        var hasBackground = DesktopWidgets.HasBackground(widget);
        if (material.FollowBoxAppearance)
            return new(inherited.Background, inherited.Accent, null,
                hasBackground ? inherited.ResolveBackgroundOpacity(acrylic) : 0, hasBackground && acrylic);
        var tint = WidgetAcrylicSettings.NormalizeBackground(material.Background) ?? WidgetAcrylicSettings.DefaultBackground;
        return new(tint, inherited.Accent, null,
            hasBackground ? acrylic ? WidgetAcrylicSettings.NormalizeOpacity(material.BackgroundOpacity) : 1 : 0,
            hasBackground && acrylic);
    }

    // Content keeps its functional accent while the material is shared.
    private static string Accent(DesktopWidgetKind kind, bool dark) => kind switch
    {
        DesktopWidgetKind.Clock => dark ? "#FFE8C07A" : "#FF7E4E10",
        DesktopWidgetKind.Calendar => dark ? "#FFFF9E8A" : "#FFB03A26",
        DesktopWidgetKind.Note => dark ? "#FFF2D48A" : "#FF7A4B0C",
        DesktopWidgetKind.Todo => dark ? "#FF8FB6FF" : "#FF1550C8",
        DesktopWidgetKind.Translation => dark ? "#FF7CDBB6" : "#FF125B44",
        DesktopWidgetKind.Weather => dark ? "#FF8FC8F5" : "#FF0E4A6E",
        DesktopWidgetKind.Performance => dark ? "#FF5FD3A6" : "#FF0F5B44",
        DesktopWidgetKind.Network => dark ? "#FF6FD0F5" : "#FF0B4E6C",
        DesktopWidgetKind.ControlCenter => dark ? "#FF93B4FF" : "#FF1F45A0",
        DesktopWidgetKind.FileShelf => dark ? "#FFE0AF7A" : "#FF7E4713",
        _ => dark ? "#FF91B9EF" : "#FF356BB3"
    };
}
