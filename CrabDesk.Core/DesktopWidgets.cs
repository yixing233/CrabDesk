using System.Text.Json.Serialization;

namespace CrabDesk.Core;

public enum DesktopWidgetKind { Clock, Calendar, Note, Todo, Translation, Weather }
public enum DesktopWidgetSize { Small, Medium, Wide, Large, Compact, Landscape }

public sealed class DesktopWidget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DesktopWidgetKind Kind { get; set; }
    public string Title { get; set; } = string.Empty;
    public string MonitorId { get; set; } = string.Empty;
    public LayoutRect Bounds { get; set; } = new(32, 32, 180, 180);
    public DesktopWidgetSize Size { get; set; } = DesktopWidgetSize.Medium;
    public bool IsLocked { get; set; }
    public bool Use24HourClock { get; set; } = true;
    public bool ShowSeconds { get; set; }
    public string Text { get; set; } = string.Empty;
    public List<WidgetTodoItem> Tasks { get; set; } = [];
    public string TargetLanguage { get; set; } = "简体中文";
    public string Translation { get; set; } = string.Empty;
    private WeatherLocation? _legacyLocation;
    private WidgetWeatherSnapshot? _legacyWeather;
    // Retain the original JSON properties so existing single-city widgets migrate on load.
    public WeatherLocation? Location
    {
        get => ActiveCity?.Location ?? _legacyLocation;
        set { if (ActiveCity is { } city && value is not null) city.Location = value; else _legacyLocation = value; }
    }
    public WidgetWeatherSnapshot? Weather
    {
        get => ActiveCity is { } city ? city.Weather : _legacyWeather;
        set { if (ActiveCity is { } city) city.Weather = value; else _legacyWeather = value; }
    }
    public List<WidgetWeatherCity> Cities { get; set; } = [];
    public Guid? ActiveCityId { get; set; }
    [JsonIgnore] public WidgetWeatherCity? ActiveCity => Cities?.FirstOrDefault(city => city is not null && city.Id == ActiveCityId) ?? Cities?.FirstOrDefault(city => city is not null);
    [JsonIgnore] public bool IsLoading { get; set; }
    [JsonIgnore] public bool IsLocating { get; set; }
    [JsonIgnore] public bool LocationAccessDenied { get; set; }
    [JsonIgnore] public string Error { get; set; } = string.Empty;
    [JsonIgnore] public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? DesktopWidgets.GetName(Kind) : Title;
}

public sealed class WidgetWeatherCity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public WeatherLocation Location { get; set; } = new("", "", "", 0, 0);
    public WidgetWeatherSnapshot? Weather { get; set; }
    public bool IsCurrentLocation { get; set; }
    [JsonIgnore] public string DisplayName => Location.ToString() + (IsCurrentLocation && Location.Name != "当前位置" ? " · 当前位置" : "");
}

public sealed class WidgetTodoItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    [JsonIgnore]
    public bool HasDateRange => StartDate.HasValue || EndDate.HasValue;
    [JsonIgnore]
    public string DateRangeText => (StartDate, EndDate) switch
    {
        ({ } start, { } end) => start == end ? start.ToString("yyyy/MM/dd") : $"{start:yyyy/MM/dd} – {end:yyyy/MM/dd}",
        ({ } start, null) => $"开始 {start:yyyy/MM/dd}",
        (null, { } end) => $"截止 {end:yyyy/MM/dd}",
        _ => string.Empty
    };
}

public sealed record WeatherLocation(string Name, string Region, string Country, double Latitude, double Longitude)
{
    public string? ProviderKey { get; init; }
    public override string ToString() => string.Join(" · ", new[] { Name, Region, Country }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
}

public sealed record WidgetWeatherSnapshot(double Temperature, double Minimum, double Maximum, int WeatherCode, DateTimeOffset FetchedAt)
{
    public double? FeelsLike { get; init; }
    public double? Humidity { get; init; }
    public double? WindSpeed { get; init; }
    public double? WindDirection { get; init; }
    public double? Pressure { get; init; }
    public double? UvIndex { get; init; }
    public double? AirQuality { get; init; }
    public double? Pm25 { get; init; }
    public string AirQualityAdvice { get; init; } = "";
    public DateTimeOffset? PublishedAt { get; init; }
    public DateTimeOffset? Sunrise { get; init; }
    public DateTimeOffset? Sunset { get; init; }
    public IReadOnlyList<WidgetHourlyWeather> Hourly { get; init; } = [];
    public IReadOnlyList<WidgetDailyWeather> Daily { get; init; } = [];
    public IReadOnlyList<WidgetWeatherAlert> Alerts { get; init; } = [];
    public IReadOnlyList<WidgetWeatherIndex> Indices { get; init; } = [];
    public string Provider { get; init; } = "Open-Meteo";
    public string? Condition { get; init; }
    public string Description => Condition ?? (WeatherCode switch
    {
        0 => "晴", 1 => "大部晴朗", 2 => "多云", 3 => "阴", 45 or 48 => "雾",
        51 or 53 or 55 => "毛毛雨", 56 or 57 => "冻毛毛雨", 61 or 63 or 65 => "雨",
        66 or 67 => "冻雨", 71 or 73 or 75 or 77 => "雪", 80 or 81 or 82 => "阵雨",
        85 or 86 => "阵雪", 95 or 96 or 99 => "雷雨", _ => "天气未知"
    });
}

public sealed record WidgetHourlyWeather(DateTimeOffset Time, double Temperature, string Condition, double? AirQuality);
public sealed record WidgetDailyWeather(DateTimeOffset Date, double Minimum, double Maximum, string Condition, double? PrecipitationProbability);
public sealed record WidgetWeatherAlert(string Title, string Detail);
public sealed record WidgetWeatherIndex(string Name, string Value)
{
    public string Type { get; init; } = "";
}

public static class DesktopWidgets
{
    public static void NormalizeWeatherCities(DesktopWidget widget)
    {
        var legacyLocation = widget.Location;
        var legacyWeather = widget.Weather;
        widget.Cities ??= [];
        widget.Cities.RemoveAll(city => city is null || city.Location is null ||
            !double.IsFinite(city.Location.Latitude) || Math.Abs(city.Location.Latitude) > 90 ||
            !double.IsFinite(city.Location.Longitude) || Math.Abs(city.Location.Longitude) > 180);
        if (widget.Cities.Count == 0 && legacyLocation is not null &&
            double.IsFinite(legacyLocation.Latitude) && Math.Abs(legacyLocation.Latitude) <= 90 &&
            double.IsFinite(legacyLocation.Longitude) && Math.Abs(legacyLocation.Longitude) <= 180)
            widget.Cities.Add(new() { Location = legacyLocation, Weather = legacyWeather });
        var ids = new HashSet<Guid>();
        foreach (var city in widget.Cities)
            if (city.Id == Guid.Empty || !ids.Add(city.Id)) { city.Id = Guid.NewGuid(); ids.Add(city.Id); }
        widget.ActiveCityId = widget.ActiveCity?.Id;
        if (widget.Cities.Count == 0) { widget.Location = null; widget.Weather = null; }
    }

    public static WidgetWeatherCity AddWeatherCity(DesktopWidget widget, WeatherLocation location, bool currentLocation = false)
    {
        NormalizeWeatherCities(widget);
        var city = widget.Cities.FirstOrDefault(c => currentLocation ? c.IsCurrentLocation :
            !c.IsCurrentLocation && Math.Abs(c.Location.Latitude - location.Latitude) < .0001 && Math.Abs(c.Location.Longitude - location.Longitude) < .0001);
        if (city is null) { city = new() { Location = location, IsCurrentLocation = currentLocation }; widget.Cities.Add(city); }
        else if (city.Location != location) { city.Location = location; city.Weather = null; }
        widget.ActiveCityId = city.Id;
        return city;
    }

    public static void RemoveWeatherCity(DesktopWidget widget, Guid cityId)
    {
        // Clear legacy storage before removing the last city to avoid resurrecting it.
        widget.Location = null;
        widget.Cities.RemoveAll(city => city.Id == cityId);
        widget.ActiveCityId = widget.ActiveCity?.Id;
        if (widget.Cities.Count == 0) { widget.Location = null; widget.Weather = null; }
    }

    public static string NormalizeText(string? value) => (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
    // Use the normalized description: Xiaomi and the legacy WMO cache use
    // different numeric weather codes for the same condition.
    public static string WeatherIconName(WidgetWeatherSnapshot weather)
    {
        var description = weather.Description;
        if (description.Contains("雹")) return "CloudHail";
        if (description.Contains("雷")) return "CloudLightning";
        if (description.Contains("雪")) return "CloudSnow";
        if (description.Contains("雨")) return "CloudRain";
        if (description.Contains("雾")) return "CloudFog";
        if (description.Contains("尘") || description.Contains("沙") || description.Contains("霾")) return "Haze";
        if (description.Contains("多云") || description.Contains("大部晴")) return "CloudSun";
        if (description.Contains("阴")) return "Cloud";
        if (description.Contains("晴")) return "Sun";
        return "CloudOff";
    }

    public static string GetName(DesktopWidgetKind kind) => kind switch
    {
        DesktopWidgetKind.Clock => "时钟", DesktopWidgetKind.Calendar => "月历", DesktopWidgetKind.Note => "便签",
        DesktopWidgetKind.Todo => "待办", DesktopWidgetKind.Translation => "翻译", DesktopWidgetKind.Weather => "天气", _ => "小组件"
    };

    public static DesktopWidgetSize DefaultSize(DesktopWidgetKind kind) => kind switch
    {
        DesktopWidgetKind.Clock => DesktopWidgetSize.Medium,
        DesktopWidgetKind.Note or DesktopWidgetKind.Translation => DesktopWidgetSize.Large,
        _ => DesktopWidgetSize.Medium
    };

    public static IReadOnlyList<DesktopWidgetSize> SupportedSizes(DesktopWidgetKind kind) => kind switch
    {
        DesktopWidgetKind.Clock => [DesktopWidgetSize.Medium, DesktopWidgetSize.Landscape],
        DesktopWidgetKind.Calendar => [DesktopWidgetSize.Medium, DesktopWidgetSize.Large],
        DesktopWidgetKind.Translation => [DesktopWidgetSize.Large],
        _ => [DesktopWidgetSize.Medium, DesktopWidgetSize.Large]
    };

    public static (double Width, double Height) Dimensions(DesktopWidgetSize size) => size switch
    {
        DesktopWidgetSize.Compact => (144, 144),
        DesktopWidgetSize.Landscape => (288, 120),
        DesktopWidgetSize.Small => (180, 84), DesktopWidgetSize.Wide => (372, 84),
        DesktopWidgetSize.Large => (372, 180), _ => (180, 180)
    };

    public static string SizeLabel(DesktopWidgetSize size)
    {
        var (width, height) = Dimensions(size);
        return $"{width} × {height} DIP";
    }

    public static IReadOnlyList<DateOnly> CalendarDays(DateOnly month)
    {
        var first = new DateOnly(month.Year, month.Month, 1);
        var offset = ((int)first.DayOfWeek + 6) % 7;
        return Enumerable.Range(0, 42).Select(index => first.AddDays(index - offset)).ToArray();
    }

    public static void Normalize(CrabDeskState state)
    {
        state.Widgets ??= [];
        var ids = new HashSet<Guid>();
        state.Widgets.RemoveAll(widget => widget is null || !Enum.IsDefined(widget.Kind));
        foreach (var widget in state.Widgets)
        {
            if (widget.Id == Guid.Empty || !ids.Add(widget.Id)) { widget.Id = Guid.NewGuid(); ids.Add(widget.Id); }
            widget.Title ??= string.Empty;
            widget.Text = NormalizeText(widget.Text);
            widget.Translation = NormalizeText(widget.Translation);
            widget.MonitorId ??= string.Empty;
            if (string.IsNullOrWhiteSpace(widget.TargetLanguage)) widget.TargetLanguage = "简体中文";
            widget.Tasks ??= [];
            NormalizeWeatherCities(widget);
            widget.Tasks.RemoveAll(task => task is null);
            var taskIds = new HashSet<Guid>();
            foreach (var task in widget.Tasks)
            {
                task.Text ??= string.Empty;
                if (task.Id == Guid.Empty || !taskIds.Add(task.Id)) { task.Id = Guid.NewGuid(); taskIds.Add(task.Id); }
            }
            if (widget.Kind == DesktopWidgetKind.Clock && widget.Size is DesktopWidgetSize.Small or DesktopWidgetSize.Wide)
                widget.Size = DesktopWidgetSize.Landscape;
            if (!SupportedSizes(widget.Kind).Contains(widget.Size)) widget.Size = DefaultSize(widget.Kind);
            var (width, height) = Dimensions(widget.Size);
            widget.Bounds = new LayoutRect(double.IsFinite(widget.Bounds.X) ? widget.Bounds.X : 32,
                double.IsFinite(widget.Bounds.Y) ? widget.Bounds.Y : 32, width, height);
        }
    }

    public static void NormalizeForMonitors(CrabDeskState state, IReadOnlyList<MonitorLayout> monitors)
    {
        if (monitors.Count == 0) return;
        foreach (var widget in state.Widgets)
        {
            var monitor = monitors.FirstOrDefault(m => string.Equals(m.Id, widget.MonitorId, StringComparison.OrdinalIgnoreCase))
                ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
            widget.MonitorId = monitor.Id;
            var local = new LayoutRect(monitor.WorkArea.X - monitor.Bounds.X, monitor.WorkArea.Y - monitor.Bounds.Y,
                monitor.WorkArea.Width, monitor.WorkArea.Height);
            widget.Bounds = widget.Bounds.Clamp(local, widget.Bounds.Width, widget.Bounds.Height);
        }
    }
}
