using System.Text.Json.Serialization;

namespace CrabDesk.Core;

public enum DesktopWidgetKind { Clock, Calendar, Note, Todo, Translation, Weather, Performance, Network, ControlCenter, Lyrics, FileShelf }
public enum DesktopWidgetSize
{
    Small, Medium, Wide, Large, Compact, Landscape, Custom,
    /// <summary>Tall panel for the control centre. Appended so saved values hold.</summary>
    Panel
}
/// <summary>
/// How a network widget presents itself. Values are persisted, so new members
/// are appended and retired ones keep their slot as a reserved gap: reusing a
/// number would silently reinterpret an existing widget's saved style.
/// </summary>
public enum NetworkWidgetDisplayMode
{
    Details = 0,
    Speeds = 1,
    /// <summary>Was "speed and chart". Kept so saved widgets still resolve.</summary>
    LegacyChart = 2,
    Curve = 3
}

public enum NetworkProcessSort { Download, Upload }

public sealed class DesktopWidget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DesktopWidgetKind Kind { get; set; }
    public string Title { get; set; } = string.Empty;
    public string MonitorId { get; set; } = string.Empty;
    public LayoutRect Bounds { get; set; } = new(32, 32, 180, 180);
    public DesktopWidgetSize Size { get; set; } = DesktopWidgetSize.Medium;
    public bool IsLocked { get; set; }
    public WidgetBackgroundMaterial BackgroundMaterial { get; set; }
    public WidgetColorTheme ColorTheme { get; set; }
    public string FontFamily { get; set; } = BoxAppearance.DefaultFontFamily;
    public double FontScale { get; set; } = 1;
    public int FontWeight { get; set; }
    public bool FontItalic { get; set; }
    public bool Use24HourClock { get; set; } = true;
    public bool ShowSeconds { get; set; }
    public string Text { get; set; } = string.Empty;
    public List<WidgetTodoItem> Tasks { get; set; } = [];
    public WidgetFileShelfMode FileShelfMode { get; set; } = WidgetFileShelfMode.Reference;
    public List<WidgetFileShelfItem> FileShelfItems { get; set; } = [];
    public FileShelfFloatingSettings FileShelfFloating { get; set; } = new();
    [JsonIgnore] public bool FileShelfBusy { get; set; }
    [JsonIgnore] public string FileShelfStatus { get; set; } = string.Empty;
    public string TargetLanguage { get; set; } = "简体中文";
    public string Translation { get; set; } = string.Empty;
    public string ControlBrightnessDisplayId { get; set; } = string.Empty;
    public string NetworkAdapterId { get; set; } = string.Empty;
    public string NetworkLatencyTarget { get; set; } = "1.1.1.1";
    public NetworkWidgetDisplayMode NetworkDisplayMode { get; set; }
    public NetworkProcessSort NetworkProcessSort { get; set; }
    [JsonIgnore] public List<WidgetNetworkRatePoint> NetworkHistory { get; } = [];
    [JsonIgnore] public WidgetPerformanceSnapshot? Performance { get; set; }
    [JsonIgnore] public WidgetNetworkSnapshot? Network { get; set; }
    [JsonIgnore] public ControlCenterSnapshot? Controls { get; set; }
    /// <summary>
    /// Forces the control centre's ink to white or to dark, overriding the
    /// automatic choice from the card colour. Null keeps the automatic
    /// behaviour, which is what every other widget uses.
    /// </summary>
    public bool? ControlCenterLightText { get; set; }
    public List<ControlCenterQuickAction>? ControlCenterQuickActions { get; set; }
    public double LyricsLineSpacing { get; set; } = 1.75;
    public double LyricsFontSize { get; set; } = 24;
    public string LyricsFontFamily { get; set; } = BoxAppearance.DefaultFontFamily;
    public int LyricsFontWeight { get; set; } = 600;
    public WidgetLyricsFontStyle LyricsFontStyle { get; set; }
    public int LyricsOffsetMilliseconds { get; set; }
    public bool LyricsWordHighlight { get; set; } = true;
    public bool? LyricsLightText { get; set; }
    public bool LyricsTransparentBackground { get; set; }
    public string LyricsLocalText { get; set; } = "";
    public string LyricsLocalFormat { get; set; } = "";
    public string LyricsLocalTrackKey { get; set; } = "";
    [JsonIgnore] public WidgetLyricsSnapshot? Lyrics { get; set; }
    [JsonIgnore] public string MonitorError { get; set; } = string.Empty;
    [JsonIgnore] public string PublicIp { get; set; } = string.Empty;
    [JsonIgnore] public string PublicIpError { get; set; } = string.Empty;
    [JsonIgnore] public bool IsPublicIpLoading { get; set; }
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
    /// <summary>True when the provider code fell back to the unknown placeholder.</summary>
    public const string UnknownCondition = "天气未知";
    public string Description => Condition ?? (WeatherCode switch
    {
        0 => "晴", 1 => "大部晴朗", 2 => "多云", 3 => "阴", 45 or 48 => "雾",
        51 or 53 or 55 => "毛毛雨", 56 or 57 => "冻毛毛雨", 61 or 63 or 65 => "雨",
        66 or 67 => "冻雨", 71 or 73 or 75 or 77 => "雪", 80 or 81 or 82 => "阵雨",
        85 or 86 => "阵雪", 95 or 96 or 99 => "雷雨", _ => UnknownCondition
    });
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasUnknownCondition => Description == UnknownCondition;
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
    public static bool HasBackground(DesktopWidget widget) =>
        widget.Kind != DesktopWidgetKind.Lyrics || !widget.LyricsTransparentBackground;

    public static bool RequiresMonitoring(DesktopWidgetKind kind) => kind is
        DesktopWidgetKind.Performance or DesktopWidgetKind.Network or DesktopWidgetKind.ControlCenter or DesktopWidgetKind.Lyrics;

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
        DesktopWidgetKind.Lyrics => "桌面歌词",
        DesktopWidgetKind.FileShelf => "文件中转站",
        DesktopWidgetKind.Clock => "时钟", DesktopWidgetKind.Calendar => "月历", DesktopWidgetKind.Note => "便签",
        DesktopWidgetKind.Todo => "待办", DesktopWidgetKind.Translation => "翻译", DesktopWidgetKind.Weather => "天气",
        DesktopWidgetKind.Performance => "性能监控", DesktopWidgetKind.Network => "网络监控",
        DesktopWidgetKind.ControlCenter => "控制中心", _ => "小组件"
    };

    public static DesktopWidgetSize DefaultSize(DesktopWidgetKind kind) => kind switch
    {
        DesktopWidgetKind.Lyrics => DesktopWidgetSize.Panel,
        DesktopWidgetKind.ControlCenter => DesktopWidgetSize.Panel,
        DesktopWidgetKind.Clock => DesktopWidgetSize.Medium,
        DesktopWidgetKind.Note or DesktopWidgetKind.Translation or DesktopWidgetKind.Network or DesktopWidgetKind.FileShelf => DesktopWidgetSize.Large,
        _ => DesktopWidgetSize.Medium
    };

    public static IReadOnlyList<DesktopWidgetSize> SupportedSizes(DesktopWidgetKind kind) => kind switch
    {
        DesktopWidgetKind.Lyrics => [DesktopWidgetSize.Large, DesktopWidgetSize.Panel, DesktopWidgetSize.Custom],
        DesktopWidgetKind.Clock => [DesktopWidgetSize.Medium, DesktopWidgetSize.Landscape],
        DesktopWidgetKind.Calendar => [DesktopWidgetSize.Medium, DesktopWidgetSize.Large],
        DesktopWidgetKind.FileShelf => [DesktopWidgetSize.Medium, DesktopWidgetSize.Large],
        DesktopWidgetKind.Note or DesktopWidgetKind.Todo => [DesktopWidgetSize.Medium, DesktopWidgetSize.Large, DesktopWidgetSize.Custom],
        DesktopWidgetKind.Translation or DesktopWidgetKind.Network => [DesktopWidgetSize.Large],
        DesktopWidgetKind.ControlCenter => [DesktopWidgetSize.Panel],
        _ => [DesktopWidgetSize.Medium, DesktopWidgetSize.Large]
    };

    public static IReadOnlyList<DesktopWidgetSize> SupportedSizes(DesktopWidget widget) =>
        widget.Kind == DesktopWidgetKind.Network && widget.NetworkDisplayMode == NetworkWidgetDisplayMode.Speeds
            ? [DesktopWidgetSize.Small, DesktopWidgetSize.Wide] : SupportedSizes(widget.Kind);

    public static DesktopWidgetSize DefaultSize(DesktopWidget widget) =>
        widget.Kind == DesktopWidgetKind.Network && widget.NetworkDisplayMode == NetworkWidgetDisplayMode.Speeds
            ? DesktopWidgetSize.Small : DefaultSize(widget.Kind);

    /// <summary>
    /// The three styles the user can pick between. The retired combined style
    /// stays readable in saved files but is not offered any more.
    /// </summary>
    public static IReadOnlyList<NetworkWidgetDisplayMode> OfferedNetworkDisplayModes { get; } =
        [NetworkWidgetDisplayMode.Details, NetworkWidgetDisplayMode.Speeds, NetworkWidgetDisplayMode.Curve];

    /// <summary>True when the widget draws the curve rather than the detail rows.</summary>
    public static bool IsNetworkCurveOnly(DesktopWidget widget) =>
        widget.Kind == DesktopWidgetKind.Network &&
        widget.NetworkDisplayMode is NetworkWidgetDisplayMode.LegacyChart or NetworkWidgetDisplayMode.Curve;

    public static string NetworkDisplayName(NetworkWidgetDisplayMode mode) => mode switch
    {
        NetworkWidgetDisplayMode.Speeds => "网速文本",
        // Both curve styles present the same way now, so they share one label:
        // showing a retired name in the picker would confuse the three-way choice.
        NetworkWidgetDisplayMode.Curve or NetworkWidgetDisplayMode.LegacyChart => "网速曲线",
        _ => "详细信息"
    };

    public static (double Width, double Height) Dimensions(DesktopWidgetSize size) => size switch
    {
        DesktopWidgetSize.Compact => (144, 144),
        DesktopWidgetSize.Landscape => (288, 120),
        DesktopWidgetSize.Small => (180, 84), DesktopWidgetSize.Wide => (372, 84),
        DesktopWidgetSize.Large => (372, 180),
        DesktopWidgetSize.Panel => (ControlCenterPresentation.CardWidth, ControlCenterPresentation.CardHeight),
        _ => (180, 180)
    };

    public static bool SupportsFreeResize(DesktopWidgetKind kind) => kind is DesktopWidgetKind.Note or DesktopWidgetKind.Todo or DesktopWidgetKind.Lyrics;

    public static (double Width, double Height) Dimensions(DesktopWidget widget) => Dimensions(widget, widget.Size);

    public static (double Width, double Height) Dimensions(DesktopWidget widget, DesktopWidgetSize size)
    {
        var preset = Dimensions(size);
        return size == DesktopWidgetSize.Custom && SupportsFreeResize(widget.Kind)
            ? (double.IsFinite(widget.Bounds.Width) && widget.Bounds.Width > 0 ? widget.Bounds.Width : preset.Width,
               double.IsFinite(widget.Bounds.Height) && widget.Bounds.Height > 0 ? widget.Bounds.Height : preset.Height)
            : preset;
    }

    public static string SizeLabel(DesktopWidgetSize size)
    {
        if (size == DesktopWidgetSize.Custom) return "自定义尺寸";
        var (width, height) = Dimensions(size);
        return $"{width} × {height} DIP";
    }

    public static string SizeLabel(DesktopWidget widget)
    {
        var (width, height) = Dimensions(widget);
        return widget.Size == DesktopWidgetSize.Custom ? $"{width:0.#} × {height:0.#} DIP · 自定义" : SizeLabel(widget, widget.Size);
    }

    public static string SizeLabel(DesktopWidget widget, DesktopWidgetSize size)
    {
        if (size == DesktopWidgetSize.Custom) return SizeLabel(size);
        var (width, height) = Dimensions(widget, size);
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
            WidgetTypography.Normalize(widget);
            WidgetAppearance.Normalize(widget);
            widget.Text = NormalizeText(widget.Text);
            widget.Translation = NormalizeText(widget.Translation);
            widget.MonitorId ??= string.Empty;
            widget.NetworkAdapterId ??= string.Empty;
            widget.LyricsLineSpacing = WidgetLyricsSettings.NormalizeLineSpacing(widget.LyricsLineSpacing);
            widget.LyricsFontSize = WidgetLyricsSettings.NormalizeFontSize(widget.LyricsFontSize);
            widget.LyricsFontFamily = WidgetLyricsSettings.NormalizeFontFamily(widget.LyricsFontFamily);
            widget.LyricsFontWeight = WidgetLyricsSettings.NormalizeFontWeight(widget.LyricsFontWeight);
            widget.LyricsFontStyle = WidgetLyricsSettings.NormalizeFontStyle(widget.LyricsFontStyle);
            widget.LyricsOffsetMilliseconds = WidgetLyricsSettings.NormalizeOffset(widget.LyricsOffsetMilliseconds);
            widget.LyricsLocalText ??= "";
            widget.LyricsLocalFormat ??= "";
            widget.LyricsLocalTrackKey ??= "";
            if (widget.Kind == DesktopWidgetKind.ControlCenter)
                widget.ControlCenterQuickActions = ControlQuickActions.Normalize(widget.ControlCenterQuickActions);
            // The combined speed-and-chart style was retired in favour of the
            // three-way choice; anything saved with it becomes the curve style,
            // which keeps its headline speeds and drops only the card title.
            if (widget.NetworkDisplayMode == NetworkWidgetDisplayMode.LegacyChart)
            {
                widget.NetworkDisplayMode = NetworkWidgetDisplayMode.Curve;
            }
            if (!Enum.IsDefined(widget.NetworkDisplayMode)) widget.NetworkDisplayMode = NetworkWidgetDisplayMode.Details;
            if (!Enum.IsDefined(widget.NetworkProcessSort)) widget.NetworkProcessSort = NetworkProcessSort.Download;
            widget.NetworkLatencyTarget = (widget.NetworkLatencyTarget ?? "1.1.1.1").Trim();
            if (!WidgetMonitorMetrics.IsValidLatencyTarget(widget.NetworkLatencyTarget)) widget.NetworkLatencyTarget = "1.1.1.1";
            if (string.IsNullOrWhiteSpace(widget.TargetLanguage)) widget.TargetLanguage = "简体中文";
            widget.Tasks ??= [];
            WidgetFileShelf.Normalize(widget);
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
            if (!SupportedSizes(widget).Contains(widget.Size)) widget.Size = DefaultSize(widget);
            var (width, height) = Dimensions(widget);
            if (widget.Size == DesktopWidgetSize.Custom)
            { width = Math.Max(WidgetSizing.MinimumWidth, width); height = Math.Max(WidgetSizing.MinimumHeight, height); }
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
            widget.Bounds = widget.Size == DesktopWidgetSize.Custom && SupportsFreeResize(widget.Kind)
                ? widget.Bounds.Clamp(local, Math.Min(WidgetSizing.MinimumWidth, local.Width), Math.Min(WidgetSizing.MinimumHeight, local.Height))
                : widget.Bounds.Clamp(local, widget.Bounds.Width, widget.Bounds.Height);
        }
    }
}
