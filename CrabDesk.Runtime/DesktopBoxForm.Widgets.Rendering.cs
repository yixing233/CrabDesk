using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using CrabDesk.Core;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm
{
    internal static RectangleF WidgetRectangle(DesktopWidget widget) => new((float)widget.Bounds.X, (float)widget.Bounds.Y,
        (float)widget.Bounds.Width, (float)widget.Bounds.Height);
    private static RectangleF WidgetMenuRectangle(RectangleF bounds) => new(bounds.Right - 32, bounds.Top, 28, 28);
    private static RectangleF WidgetMenuRectangle(DesktopWidget widget, double radius = 8)
    {
        var bounds = WidgetRectangle(widget);
        return widget.Kind == DesktopWidgetKind.Todo
            ? new(bounds.Right - 36, bounds.Y + 4, 24, 24)
            : widget.Kind == DesktopWidgetKind.Weather
                ? WeatherRectangle(widget, WeatherPresentation(widget, radius).Menu)
            : WidgetMenuRectangle(bounds);
    }
    private static RectangleF WidgetBodyRectangle(DesktopWidget widget)
    {
        var bounds = WidgetRectangle(widget);
        var header = widget.Kind == DesktopWidgetKind.Clock ? 0 : 28;
        return new(bounds.X + 8, bounds.Y + header + 8, bounds.Width - 16, bounds.Height - header - 16);
    }

    private void DrawWidgets(Graphics graphics, RectangleF clipBounds)
    {
        foreach (var widget in DesktopWidgetItems.Where(w => WidgetRectangle(w).IntersectsWith(clipBounds)))
            DrawWidget(graphics, widget);
    }

    private void DrawWidget(Graphics graphics, DesktopWidget widget)
    {
        var bounds = WidgetRectangle(widget);
        var settings = _runtime.State.Settings.Appearance;
        var appearance = _runtime.State.Boxes.FirstOrDefault()?.Appearance ?? _runtime.State.Settings.DefaultBoxAppearance;
        var background = ParseOpaqueColor(appearance.Background);
        var text = ResolveAutoTextColor(background);
        var accent = ParseOpaqueColor(appearance.Accent);
        var muted = UsesLightText(background) ? Color.FromArgb(237, 240, 245) : Color.FromArgb(52, 59, 69);
        var tint = ResolveBoxTintOpacity(appearance.Opacity, _usesAcrylicBackground);
        using var path = RoundedRectangle(RectangleF.Inflate(bounds, -.5f, -.5f), (float)settings.CornerRadius);
        using var fill = new SolidBrush(ApplyOpacity(background, tint));
        graphics.FillPath(fill, path);
        var state = graphics.Save();
        graphics.SetClip(path, CombineMode.Intersect);
        if (widget.Kind is not (DesktopWidgetKind.Clock or DesktopWidgetKind.Calendar or DesktopWidgetKind.Todo or DesktopWidgetKind.Weather) &&
            widget.Kind != DesktopWidgetKind.ControlCenter &&
            !(widget.Kind == DesktopWidgetKind.Network &&
              widget.NetworkDisplayMode is NetworkWidgetDisplayMode.Speeds or NetworkWidgetDisplayMode.Curve))
        {
            var titleMargin = widget.Kind is DesktopWidgetKind.Todo or DesktopWidgetKind.Weather ? 60 : 32;
            WidgetText(graphics, widget.DisplayTitle, new(bounds.X + titleMargin, bounds.Y, bounds.Width - titleMargin * 2, 28), text, 13.333f, true, true);
        }
        if (widget.Kind == DesktopWidgetKind.Weather) DrawWeatherLocationButton(graphics, widget, text, accent);
        var body = WidgetBodyRectangle(widget);
        switch (widget.Kind)
        {
            case DesktopWidgetKind.Clock: DrawClockWidget(graphics, widget, body, text, muted, DateTime.Now); break;
            case DesktopWidgetKind.Calendar: DrawCalendarWidget(graphics, widget, body, text, muted, accent); break;
            case DesktopWidgetKind.Note: DrawTextWidget(graphics, widget, body, text, muted); break;
            case DesktopWidgetKind.Todo: DrawTodoWidget(graphics, widget, body, text, muted, accent); break;
            case DesktopWidgetKind.Translation: DrawTranslationWidget(graphics, widget, body, text); break;
            case DesktopWidgetKind.Weather: DrawWeatherWidget(graphics, widget, body, text, muted); break;
            case DesktopWidgetKind.Performance: DrawPerformanceWidget(graphics, widget, text, muted, accent); break;
            case DesktopWidgetKind.Network: DrawNetworkWidget(graphics, widget, text, muted, accent); break;
            case DesktopWidgetKind.ControlCenter: DrawControlCenterWidget(graphics, widget, text, muted, accent, background); break;
        }
        if (_hoveredWidgetId == widget.Id)
        {
            var menu = WidgetMenuRectangle(widget, settings.CornerRadius);
            var hovered = _hoveredWidgetMenuId == widget.Id;
            if (hovered)
            {
                using var hoverPath = RoundedRectangle(RectangleF.Inflate(menu, -4, -4), (float)Math.Min(10, settings.IconHoverCornerRadius));
                using var hover = new SolidBrush(Color.FromArgb(18, text));
                graphics.FillPath(hover, hoverPath);
            }
            using var dots = new SolidBrush(Color.FromArgb(hovered ? 220 : 170, text));
            var centerX = menu.X + menu.Width / 2;
            var centerY = menu.Y + menu.Height / 2;
            for (var index = -1; index <= 1; index++)
                graphics.FillEllipse(dots, centerX + index * 4 - .9f, centerY - .9f, 1.8f, 1.8f);
        }
        graphics.Restore(state);
        if (settings.ShowBorder)
        {
            using var pen = new Pen(BoxBorderStyle.ApplyTint(BoxBorderStyle.ResolveStroke(background, UsesLightText(background),
                settings.BorderColor, settings.BorderOpacity), tint), BoxBorderStyle.ResolveWidth(settings.BorderWidth));
            pen.Alignment = PenAlignment.Inset;
            graphics.DrawPath(pen, path);
        }
    }

    /// <summary>
    /// Width of a string at a given size, using the same font the widget draws
    /// with. Used to place text that follows another run on the same line, such
    /// as a temperature after a metric label.
    /// </summary>
    internal static float MeasureWidgetText(Graphics graphics, string value, float size, bool bold = false)
    {
        using var font = new Font(BoxAppearance.DefaultFontFamily, size,
            bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        using var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap };
        return string.IsNullOrEmpty(value) ? 0 : graphics.MeasureString(value, font, int.MaxValue, format).Width;
    }

    private static void WidgetText(Graphics graphics, string value, RectangleF bounds, Color color, float size = 12,
        bool bold = false, bool centered = false, bool multiline = false, bool strike = false, bool rightAligned = false)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        using var font = new Font(BoxAppearance.DefaultFontFamily, size,
            (bold ? FontStyle.Bold : FontStyle.Regular) | (strike ? FontStyle.Strikeout : FontStyle.Regular), GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat
        {
            Alignment = rightAligned ? StringAlignment.Far : centered ? StringAlignment.Center : StringAlignment.Near,
            LineAlignment = multiline ? StringAlignment.Near : StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = multiline ? 0 : StringFormatFlags.NoWrap
        };
        graphics.DrawString(value, font, brush, bounds, format);
    }

    private DateOnly WidgetMonth(DesktopWidget widget) => _widgetMonths.GetValueOrDefault(widget.Id,
        new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1));

    internal readonly record struct CalendarHeaderLayout(RectangleF Title, RectangleF Previous, RectangleF Today, RectangleF Next);

    internal static CalendarHeaderLayout GetCalendarHeaderLayout(DesktopWidget widget)
    {
        var bounds = WidgetRectangle(widget);
        bounds.Width = Math.Min(bounds.Width, 180);
        var next = new RectangleF(WidgetMenuRectangle(bounds).Left - 24, bounds.Y + 2, 20, 24);
        var today = new RectangleF(next.Left - 26, next.Y, 24, 24);
        var previous = new RectangleF(today.Left - 22, next.Y, 20, 24);
        return new(new(bounds.X + 8, bounds.Y, previous.Left - bounds.X - 12, 28), previous, today, next);
    }

    private static bool IsCalendarNavigationPoint(DesktopWidget widget, PointF point)
    {
        if (widget.Kind != DesktopWidgetKind.Calendar) return false;
        var header = GetCalendarHeaderLayout(widget);
        return header.Previous.Contains(point) || header.Today.Contains(point) || header.Next.Contains(point);
    }

    private void DrawCalendarWidget(Graphics graphics, DesktopWidget widget, RectangleF body, Color text, Color muted, Color accent)
    {
        body = CalendarBodyRectangle(widget);
        var month = WidgetMonth(widget);
        var header = GetCalendarHeaderLayout(widget);
        WidgetText(graphics, month.ToString("yyyy.MM", CultureInfo.InvariantCulture), header.Title, text, 12.5f, true);
        foreach (var button in new[] { header.Previous, header.Today, header.Next })
        {
            var hovered = button == _hoveredCalendarButton;
            if (button == header.Today || hovered)
            {
                using var buttonPath = RoundedRectangle(button, (float)Math.Min(6, _runtime.State.Settings.Appearance.CornerRadius));
                using var buttonFill = new SolidBrush(Color.FromArgb(hovered ? 30 : 14, text));
                graphics.FillPath(buttonFill, buttonPath);
            }
            if (button == header.Today) WidgetText(graphics, "今", button, text, 11.5f, centered: true);
            else LucideRuntimeIcons.Draw(graphics, button == header.Previous ? LucideRuntimeIcon.ChevronLeft : LucideRuntimeIcon.ChevronRight, button, text, 12);
        }
        var days = DesktopWidgets.CalendarDays(month);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var width = body.Width / 7;
        var height = (body.Height - 14) / 6;
        var labels = new[] { "一", "二", "三", "四", "五", "六", "日" };
        for (var col = 0; col < 7; col++) WidgetText(graphics, labels[col], new(body.X + col * width, body.Y, width, 14), muted, 11, centered: true);
        for (var index = 0; index < days.Count; index++)
        {
            var cell = CalendarDayRectangle(widget, index);
            var selected = days[index] == today;
            if (_calendarToolTipWidgetId == widget.Id && _hoveredCalendarDate == days[index])
            {
                using var hoverPath = RoundedRectangle(RectangleF.Inflate(cell, -1, -1), (float)Math.Min(4, _runtime.State.Settings.Appearance.CornerRadius));
                using var hoverFill = new SolidBrush(Color.FromArgb(24, text));
                graphics.FillPath(hoverFill, hoverPath);
            }
            if (selected)
            {
                using var brush = new SolidBrush(accent);
                var diameter = Math.Min(20, Math.Min(width, height));
                graphics.FillEllipse(brush, cell.X + (width - diameter) / 2, cell.Y + (height - diameter) / 2, diameter, diameter);
            }
            var color = selected ? ResolveAutoTextColor(accent) : days[index].Month == month.Month ? text : Color.FromArgb(140, muted);
            WidgetText(graphics, days[index].Day.ToString(CultureInfo.InvariantCulture), cell, color, 11.5f, selected, true);
        }
        DrawCalendarUpcomingEvents(graphics, widget, text, muted, accent);
    }

    private void DrawTextWidget(Graphics graphics, DesktopWidget widget, RectangleF body, Color text, Color muted)
    {
        var value = DesktopWidgets.NormalizeText(widget.Text);
        var footer = "点击编辑 · 自动保存";
        if (string.IsNullOrWhiteSpace(value)) value = "点击这里，写下第一条记录…";
        if (!_noteTextVisuals.TryGetValue(widget.Id, out var visual)) _noteTextVisuals[widget.Id] = visual = new();
        _widgetScroll[widget.Id] = visual.Draw(graphics, value, NoteFieldRectangle(widget), text,
            _widgetScroll.GetValueOrDefault(widget.Id), insets: new System.Windows.Thickness(8, 6, 8, 6));
        WidgetText(graphics, footer, new(body.X, body.Bottom - 18, body.Width, 18), muted, 11.333f);
    }

    private void DrawWeatherWidget(Graphics graphics, DesktopWidget widget, RectangleF body, Color text, Color muted)
    {
        var presentation = WeatherPresentation(widget, _runtime.State.Settings.Appearance.CornerRadius);
        body = WeatherRectangle(widget, presentation.Body);
        if (widget.Location is null)
        {
            WidgetText(graphics, "添加天气城市", new(body.X, body.Y + 6, body.Width, 26), text, 15, true, true);
            var button = WeatherEmptyLocationRectangle(widget, _runtime.State.Settings.Appearance.CornerRadius);
            using var path = RoundedRectangle(button, 6);
            using var fill = new SolidBrush(Color.FromArgb(widget.IsLocating ? 12 : 25, text));
            graphics.FillPath(fill, path);
            WidgetText(graphics, widget.IsLocating ? "正在定位…" : "使用当前位置", button, text, 12, centered: true);
            var hint = widget.IsLocating ? "正在获取系统位置…" : widget.LocationAccessDenied ? "定位未授权\n右键打开系统定位设置" :
                !string.IsNullOrEmpty(widget.Error) ? widget.Error : "点击右上位置图标\n搜索并添加城市";
            WidgetText(graphics, hint, new(body.X, body.Y + 78, body.Width, 36), muted, 10.5f, centered: true, multiline: true);
        }
        else
        {
            var layout = GetWeatherLayout(widget, _runtime.State.Settings.Appearance.CornerRadius);
            if (widget.Weather is { } weather)
            {
                WidgetText(graphics, $"{weather.Temperature:0.#}°", layout.Temperature, text, widget.Cities.Count > 1 && body.Width < 300 ? 38 : 40, true);
                var icon = Enum.Parse<LucideRuntimeIcon>(DesktopWidgets.WeatherIconName(weather));
                var lightText = text.GetBrightness() > .5;
                var iconColor = icon is LucideRuntimeIcon.Sun or LucideRuntimeIcon.CloudSun or LucideRuntimeIcon.CloudLightning
                    ? lightText ? Color.FromArgb(242, 184, 75) : Color.FromArgb(156, 97, 0)
                    : icon is LucideRuntimeIcon.CloudRain or LucideRuntimeIcon.CloudSnow or LucideRuntimeIcon.CloudHail
                        ? lightText ? Color.FromArgb(104, 193, 226) : Color.FromArgb(29, 106, 150) : muted;
                LucideRuntimeIcons.Draw(graphics, icon, layout.Icon, iconColor, 40);
                if (weather.FeelsLike is { } feelsLike)
                    DrawWeatherMetric(graphics, LucideRuntimeIcon.Thermometer, $"{feelsLike:0}°", layout.FeelsLike, muted);
                WidgetText(graphics, $"{weather.Description} · {weather.Minimum:0}° / {weather.Maximum:0}°", layout.Forecast, muted, 12);
                if (weather.Humidity is { } humidity)
                    DrawWeatherMetric(graphics, LucideRuntimeIcon.Droplets, $"{(body.Width >= 300 ? "湿度 " : "")}{humidity:0}%", layout.Humidity, muted);
                if (weather.WindSpeed is { } wind)
                    DrawWeatherMetric(graphics, LucideRuntimeIcon.Wind, $"{(body.Width >= 300 ? "风速 " : "")}{wind:0} km/h", layout.Wind, muted);
            }
            else WidgetText(graphics, widget.IsLoading ? "正在获取天气…" : "暂无天气数据", new(body.X, body.Y + 28, body.Width, 40), muted);
            var status = widget.IsLocating ? "正在定位当前位置…" : widget.IsLoading ? "正在更新…" : widget.LocationAccessDenied ? "定位未授权，请开启位置服务" :
                !string.IsNullOrEmpty(widget.Error) ? widget.Error : widget.Weather?.AirQuality is { } aqi ? $"AQI {aqi:0}" : "查看天气详情";
            var statusIcon = widget.IsLocating || widget.IsLoading ? LucideRuntimeIcon.RefreshCw : widget.LocationAccessDenied || !string.IsNullOrEmpty(widget.Error)
                ? LucideRuntimeIcon.CircleAlert : widget.Weather?.AirQuality is not null ? LucideRuntimeIcon.Gauge : LucideRuntimeIcon.ChevronRight;
            using var statusPath = RoundedRectangle(layout.Status, 8);
            using var statusFill = new SolidBrush(Color.FromArgb(14, text));
            graphics.FillPath(statusFill, statusPath);
            DrawWeatherMetric(graphics, statusIcon, status,
                new(layout.Status.X + 8, layout.Status.Y, layout.Status.Width - 32, layout.Status.Height), muted);
            LucideRuntimeIcons.Draw(graphics, LucideRuntimeIcon.ChevronRight,
                new(layout.Status.Right - 20, layout.Status.Y, 12, layout.Status.Height), muted, 12);
        }
        if (widget.Cities.Count > 1)
        {
            WidgetText(graphics, "‹", WeatherRectangle(widget, presentation.Previous), text, 18, centered: true);
            WidgetText(graphics, "›", WeatherRectangle(widget, presentation.Next), text, 18, centered: true);
            var index = widget.Cities.FindIndex(city => city.Id == widget.ActiveCityId) + 1;
            WidgetText(graphics, $"{index}/{widget.Cities.Count}", WeatherRectangle(widget, presentation.Counter), muted, 11, centered: true);
        }
    }
}
