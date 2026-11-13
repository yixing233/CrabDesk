using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using CrabDesk.Core;

namespace CrabDesk.Runtime;

internal abstract partial class WidgetSurfaceController
{
    protected void DrawWideWeather(Graphics graphics, DesktopWidget widget, WidgetWeatherPresentation.Layout layout, Color text, Color muted)
    {
        using var widgetView = EnterWidgetView();
        var now = DateTimeOffset.Now;
        var weather = widget.Weather;
        WidgetColorIcons.Draw(graphics, WidgetIconCatalog.GetWeatherName(weather, now), WeatherRectangle(widget, layout.Icon));
        WidgetText(graphics, weather is null ? "—°" : $"{weather.Temperature:0.#}°",
            WeatherRectangle(widget, layout.Temperature), text, 36, bold: true);
        if (weather is not null)
        {
            WidgetText(graphics, $"高 {weather.Maximum:0}°",
                WeatherRectangle(widget, layout.Forecast with { Height = layout.City.Height }), muted, 13);
            WidgetText(graphics, $"低 {weather.Minimum:0}°",
                WeatherRectangle(widget, layout.Forecast with { Y = layout.Description.Y, Height = layout.Description.Height }), muted, 11.5f);
        }
        WidgetText(graphics, weather?.Description ?? (widget.IsLoading ? "正在获取天气…" : "暂无天气数据"),
            WeatherRectangle(widget, layout.Description), muted, 11.5f);
        var status = widget.IsLocating ? "正在定位…" : widget.IsLoading ? "正在更新…" :
            widget.LocationAccessDenied ? "定位未授权" : !string.IsNullOrEmpty(widget.Error) ? widget.Error :
            weather?.AirQuality is { } aqi ? $"AQI {aqi:0}" : "查看天气详情";
        var statusBounds = WeatherRectangle(widget, layout.Status);
        var icon = widget.IsLocating || widget.IsLoading ? TablerRuntimeIcon.RefreshCw :
            widget.LocationAccessDenied || !string.IsNullOrEmpty(widget.Error) ? TablerRuntimeIcon.CircleAlert : TablerRuntimeIcon.Gauge;
        TablerRuntimeIcons.Draw(graphics, icon, new(statusBounds.X, statusBounds.Y, 12, statusBounds.Height), muted, 12);
        WidgetText(graphics, status, new(statusBounds.X + 17, statusBounds.Y, statusBounds.Width - 17, statusBounds.Height), muted, 10);
        IReadOnlyList<WidgetHourlyWeather> hours = weather is null ? [] : WidgetWeatherPresentation.UpcomingHours(weather, now);
        if (hours.Count == 0)
        {
            var first = layout.Hours[0].Time;
            var last = layout.Hours[^1].Temperature;
            WidgetText(graphics, widget.IsLoading ? "正在获取逐小时预报…" : "暂无逐小时预报",
                WeatherRectangle(widget, new(first.X, first.Y, last.X + last.Width - first.X, last.Y + last.Height - first.Y)), muted, 12, centered: true);
            return;
        }
        for (var index = 0; index < hours.Count; index++)
        {
            var hour = hours[index];
            var cell = layout.Hours[index];
            WidgetText(graphics, hour.Time.ToString("HH时", CultureInfo.InvariantCulture), WeatherRectangle(widget, cell.Time), muted, 10.5f, centered: true);
            WidgetColorIcons.Draw(graphics, WidgetIconCatalog.GetHourlyWeatherName(hour, weather!), WeatherRectangle(widget, cell.Icon));
            WidgetText(graphics, $"{hour.Temperature:0}°", WeatherRectangle(widget, cell.Temperature), text, 12.5f, bold: true, centered: true);
        }
    }
}
