namespace CrabDesk.Core;

public sealed class WidgetCenterSettings
{
    public bool Enabled { get; set; }
    public ApplicationThemeMode ThemeMode { get; set; } = ApplicationThemeMode.System;
    public double Width { get; set; } = 840;
    public double Height { get; set; } = 680;
    public string MonitorId { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public bool HasPosition { get; set; }

    public static WidgetCenterSettings Normalize(WidgetCenterSettings? settings)
    {
        settings ??= new();
        if (!Enum.IsDefined(settings.ThemeMode)) settings.ThemeMode = ApplicationThemeMode.System;
        settings.Width = double.IsFinite(settings.Width) && settings.Width > 0 ? settings.Width : 840;
        settings.Height = double.IsFinite(settings.Height) && settings.Height > 0 ? settings.Height : 680;
        settings.MonitorId = settings.MonitorId?.Trim() ?? string.Empty;
        if (!double.IsFinite(settings.X) || !double.IsFinite(settings.Y))
        {
            settings.X = 0;
            settings.Y = 0;
            settings.HasPosition = false;
        }
        return settings;
    }
}
