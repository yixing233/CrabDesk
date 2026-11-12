namespace CrabDesk.Core;

/// <summary>Shared asset names for the widget gallery and desktop renderer.</summary>
public static class WidgetIconCatalog
{
    public static string GetName(DesktopWidgetKind kind) => kind switch
    {
        DesktopWidgetKind.Clock => "clock",
        DesktopWidgetKind.Calendar => "calendar",
        DesktopWidgetKind.Note => "note",
        DesktopWidgetKind.Todo => "todo",
        DesktopWidgetKind.Translation => "translation",
        DesktopWidgetKind.Weather => "weather",
        DesktopWidgetKind.Performance => "performance",
        DesktopWidgetKind.Network => "network",
        DesktopWidgetKind.ControlCenter => "control-center",
        DesktopWidgetKind.Lyrics => "lyrics",
        DesktopWidgetKind.FileShelf => "file-shelf",
        _ => "file-generic"
    };

    public static string GetWeatherName(WidgetWeatherSnapshot? weather, DateTimeOffset now)
    {
        if (weather is null) return "weather-unavailable";
        return DesktopWidgets.WeatherIconName(weather) switch
        {
            "Sun" => IsNight(weather, now) ? "weather-clear-night" : "weather-clear",
            "CloudSun" => "weather-partly-cloudy",
            "Cloud" => "weather-cloudy",
            "CloudRain" => "weather-rain",
            "CloudSnow" => "weather-snow",
            "CloudLightning" => "weather-thunderstorm",
            "CloudFog" or "Haze" => "weather-fog",
            "CloudHail" => "weather-hail",
            _ => "weather-unavailable"
        };
    }

    public static string GetHourlyWeatherName(WidgetHourlyWeather hour, WidgetWeatherSnapshot weather) =>
        GetWeatherName(weather with { Condition = hour.Condition }, hour.Time);

    private static bool IsNight(WidgetWeatherSnapshot weather, DateTimeOffset now)
    {
        if (weather.Sunrise is not { } sunrise || weather.Sunset is not { } sunset || sunset <= sunrise)
            return false;
        // Sunrise carries the city's UTC offset. Stale or cross-day solar data
        // must not turn a currently sunny city into a night icon.
        var cityNow = now.ToOffset(sunrise.Offset);
        if (cityNow.Date != sunrise.Date || sunset.ToOffset(sunrise.Offset).Date != sunrise.Date)
            return false;
        return now < sunrise || now >= sunset;
    }
}
