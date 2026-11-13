using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using CrabDesk.Core;

namespace CrabDesk.Runtime;

internal abstract partial class WidgetSurfaceController
{
    internal static RectangleF WidgetRectangle(DesktopWidget widget) => new((float)GetWidgetBounds(widget).X, (float)GetWidgetBounds(widget).Y,
        (float)GetWidgetBounds(widget).Width, (float)GetWidgetBounds(widget).Height);
    protected static RectangleF WidgetMenuRectangle(RectangleF bounds) => new(bounds.Right - 32, bounds.Top, 28, 28);
    protected static RectangleF WidgetMenuRectangle(DesktopWidget widget, double radius = 8)
    {
        var bounds = WidgetRectangle(widget);
        if (widget.Kind == DesktopWidgetKind.FileShelf)
        {
            var add = WidgetFileShelfPresentation.AddButton(radius);
            return new(bounds.Right - (float)(add.X + add.Width), bounds.Y + (float)add.Y, (float)add.Width, (float)add.Height);
        }
        return widget.Kind == DesktopWidgetKind.ControlCenter
            ? new(bounds.Right - 32, bounds.Y, 28, (float)ControlCenterPresentation.Body(bounds.Width, bounds.Height, radius).Y)
            : widget.Kind == DesktopWidgetKind.Todo
            ? new(bounds.Right - 36, bounds.Y + 4, 24, 24)
            : widget.Kind == DesktopWidgetKind.Weather
                ? WeatherRectangle(widget, WeatherPresentation(widget, radius).Menu)
            : WidgetMenuRectangle(bounds);
    }
    protected static RectangleF WidgetBodyRectangle(DesktopWidget widget)
    {
        var bounds = WidgetRectangle(widget);
        var header = widget.Kind == DesktopWidgetKind.Clock ? 0 :
            widget.Kind == DesktopWidgetKind.Weather && bounds.Width >= 300 ? 12 : 28;
        return new(bounds.X + 8, bounds.Y + header + 8, bounds.Width - 16, bounds.Height - header - 16);
    }

    protected void DrawWidgets(Graphics graphics, RectangleF clipBounds)
    {
        using var widgetView = EnterWidgetView();
        foreach (var widget in DesktopWidgetItems.Where(w => WidgetRectangle(w).IntersectsWith(clipBounds)))
            DrawWidget(graphics, widget);
    }

    // Popups and inline editors that belong to one card borrow its resolved
    // surface, so a solid card keeps solid chrome and an acrylic one keeps blur.
    protected BoxAppearance WidgetBoxAppearance(DesktopWidget widget, out bool acrylic)
    {
        using var widgetView = EnterWidgetView();
        var surface = ResolveWidgetSurface(widget);
        acrylic = surface.Acrylic;
        return new BoxAppearance
        {
            Background = surface.Background,
            Accent = surface.Accent,
            BackgroundOpacity = surface.Opacity
        };
    }

    // Only widgets that actually resolved to acrylic paint a translucent card;
    // the rest stay opaque so a solid card never shows the wallpaper through.
    protected WidgetSurfaceAppearance ResolveWidgetSurface(DesktopWidget widget) =>
        WidgetAppearance.Resolve(widget,
            _runtime.State.Boxes.FirstOrDefault()?.Appearance ?? _runtime.State.Settings.DefaultBoxAppearance,
            WidgetUsesAcrylic(widget),
            widgetAcrylic: _runtime.State.Settings.Appearance.WidgetAcrylic, forceSolid: ForceSolidAppearance,
            colorTheme: SurfaceColorTheme);

    protected void DrawWidget(Graphics graphics, DesktopWidget widget)
    {
        using var widgetView = EnterWidgetView();
        using var typography = WidgetTextRendering.Begin(graphics, widget);
        var bounds = WidgetRectangle(widget);
        var settings = _runtime.State.Settings.Appearance;
        var appearance = ResolveWidgetSurface(widget);
        var background = ParseOpaqueColor(appearance.Background);
        var text = ResolveAutoTextColor(background);
        var accent = ParseOpaqueColor(appearance.Accent);
        var muted = UsesLightText(background) ? Color.FromArgb(237, 240, 245) : Color.FromArgb(52, 59, 69);
        if (IsWidgetSolid(widget))
            muted = UsesLightText(background) ? Color.FromArgb(176, 183, 195) : Color.FromArgb(102, 113, 128);
        if (!ForceSolidAppearance && widget.Kind == DesktopWidgetKind.ControlCenter && widget.ControlCenterLightText is { } lightText)
        {
            text = lightText ? Color.White : Color.FromArgb(31, 35, 41);
            muted = lightText ? Color.FromArgb(214, 220, 230) : Color.FromArgb(70, 78, 90);
        }
        var tint = appearance.Opacity;
        using var path = RoundedRectangle(RectangleF.Inflate(bounds, -.5f, -.5f), (float)WidgetCornerRadius);
        var hasBackground = WidgetHasBackground(widget);
        if (hasBackground)
        {
            if (appearance.GradientEnd is { } gradientEnd)
            {
                using var fill = new LinearGradientBrush(bounds, ApplyOpacity(background, tint),
                    ApplyOpacity(ParseOpaqueColor(gradientEnd), tint), LinearGradientMode.ForwardDiagonal);
                graphics.FillPath(fill, path);
            }
            else
            {
                using var fill = new SolidBrush(ApplyOpacity(background, tint));
                graphics.FillPath(fill, path);
            }
        }
        var state = graphics.Save();
        graphics.SetClip(path, CombineMode.Intersect);
        if (widget.Kind is not (DesktopWidgetKind.Clock or DesktopWidgetKind.Calendar or DesktopWidgetKind.Todo or DesktopWidgetKind.Weather) &&
            widget.Kind is not (DesktopWidgetKind.ControlCenter or DesktopWidgetKind.Lyrics) &&
            !(widget.Kind == DesktopWidgetKind.Network &&
              widget.NetworkDisplayMode is NetworkWidgetDisplayMode.Speeds or NetworkWidgetDisplayMode.Curve))
        {
            var utilityTitle = IsWidgetSolid(widget) &&
                widget.Kind is DesktopWidgetKind.Performance or DesktopWidgetKind.Network;
            var titleMargin = utilityTitle ? 12 : widget.Kind == DesktopWidgetKind.FileShelf
                ? (float)(WidgetFileShelfPresentation.AddButton(WidgetCornerRadius).X + 32)
                : widget.Kind is DesktopWidgetKind.Todo or DesktopWidgetKind.Weather ? 60 : 32;
            var titleColor = utilityTitle ? muted : widget.Kind == DesktopWidgetKind.Note ? ContentTone("note", text) : text;
            WidgetText(graphics, widget.DisplayTitle, new(bounds.X + titleMargin, bounds.Y, bounds.Width - titleMargin * 2, 28),
                titleColor, utilityTitle ? 11.5f : 13.333f, true, !utilityTitle);
        }
        if (widget.Kind == DesktopWidgetKind.Weather) DrawWeatherLocationButton(graphics, widget, text, accent);
        var body = WidgetBodyRectangle(widget);
        switch (widget.Kind)
        {
            case DesktopWidgetKind.Clock: DrawClockWidget(graphics, widget, body, text, muted, DateTime.Now, background, accent); break;
            case DesktopWidgetKind.Calendar: DrawCalendarWidget(graphics, widget, body, text, muted, accent); break;
            case DesktopWidgetKind.Note: DrawTextWidget(graphics, widget, body, text, muted); break;
            case DesktopWidgetKind.Todo: DrawTodoWidget(graphics, widget, body, text, muted, accent); break;
            case DesktopWidgetKind.Translation: DrawTranslationWidget(graphics, widget, body, text); break;
            case DesktopWidgetKind.Weather: DrawWeatherWidget(graphics, widget, body, text, muted); break;
            case DesktopWidgetKind.Performance: DrawPerformanceWidget(graphics, widget, text, muted, accent); break;
            case DesktopWidgetKind.Network: DrawNetworkWidget(graphics, widget, text, muted, accent); break;
            case DesktopWidgetKind.ControlCenter: DrawControlCenterWidget(graphics, widget, text, muted, accent, background); break;
            case DesktopWidgetKind.Lyrics: DrawLyricsWidget(graphics, widget, text, accent); break;
            case DesktopWidgetKind.FileShelf: DrawFileShelfWidget(graphics, widget, text, muted, accent); break;
        }
        if (_hoveredWidgetId == widget.Id)
        {
            var menu = WidgetMenuRectangle(widget, WidgetCornerRadius);
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
        if (hasBackground && settings.ShowBorder)
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
        using var font = WidgetTextRendering.CreateFont(graphics, size, bold);
        using var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap };
        return string.IsNullOrEmpty(value) ? 0 : graphics.MeasureString(value, font, int.MaxValue, format).Width;
    }

    protected static void WidgetText(Graphics graphics, string value, RectangleF bounds, Color color, float size = 12,
        bool bold = false, bool centered = false, bool multiline = false, bool strike = false, bool rightAligned = false)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        using var font = WidgetTextRendering.CreateFont(graphics, size, bold, strike);
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

    protected DateOnly WidgetMonth(DesktopWidget widget) => _widgetMonths.GetValueOrDefault(widget.Id,
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

    protected static bool IsCalendarNavigationPoint(DesktopWidget widget, PointF point)
    {
        if (widget.Kind != DesktopWidgetKind.Calendar) return false;
        var header = GetCalendarHeaderLayout(widget);
        return header.Previous.Contains(point) || header.Today.Contains(point) || header.Next.Contains(point);
    }

    protected void DrawCalendarWidget(Graphics graphics, DesktopWidget widget, RectangleF body, Color text, Color muted, Color accent)
    {
        using var widgetView = EnterWidgetView();
        body = CalendarBodyRectangle(widget);
        var month = WidgetMonth(widget);
        var header = GetCalendarHeaderLayout(widget);
        WidgetText(graphics, month.ToString("yyyy.MM", CultureInfo.InvariantCulture), header.Title, text, 12.5f, true);
        foreach (var button in new[] { header.Previous, header.Today, header.Next })
        {
            var hovered = button == _hoveredCalendarButton;
            if (button == header.Today || hovered)
            {
                using var buttonPath = RoundedRectangle(button, (float)Math.Min(6, WidgetCornerRadius));
                using var buttonFill = new SolidBrush(Color.FromArgb(hovered ? 30 : 14, text));
                graphics.FillPath(buttonFill, buttonPath);
            }
            if (button == header.Today) WidgetText(graphics, "今", button, text, 11.5f, centered: true);
            else TablerRuntimeIcons.Draw(graphics, button == header.Previous ? TablerRuntimeIcon.ChevronLeft : TablerRuntimeIcon.ChevronRight, button, text, 12);
        }
        var days = DesktopWidgets.CalendarDays(month);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var width = body.Width / 7;
        var height = (body.Height - 14) / 6;
        var labels = new[] { "一", "二", "三", "四", "五", "六", "日" };
        for (var col = 0; col < 7; col++) WidgetText(graphics, labels[col], new(body.X + col * width, body.Y, width, 14), col >= 5 ? ContentTone("content-muted", text) : muted, 11, centered: true);
        for (var index = 0; index < days.Count; index++)
        {
            var cell = CalendarDayRectangle(widget, index);
            var selected = days[index] == today;
            if (_calendarToolTipWidgetId == widget.Id && _hoveredCalendarDate == days[index])
            {
                using var hoverPath = RoundedRectangle(RectangleF.Inflate(cell, -1, -1), (float)Math.Min(4, WidgetCornerRadius));
                using var hoverFill = new SolidBrush(Color.FromArgb(24, text));
                graphics.FillPath(hoverFill, hoverPath);
            }
            if (selected)
            {
                using var brush = new SolidBrush(accent);
                var diameter = Math.Min(20, Math.Min(width, height));
                graphics.FillEllipse(brush, cell.X + (width - diameter) / 2, cell.Y + (height - diameter) / 2, diameter, diameter);
            }
            var color = selected ? ResolveAutoTextColor(accent) : days[index].Month == month.Month
                ? index % 7 >= 5 ? ContentTone("content-muted", text) : text : Color.FromArgb(140, muted);
            WidgetText(graphics, days[index].Day.ToString(CultureInfo.InvariantCulture), cell, color, 11.5f, selected, true);
        }
        DrawCalendarUpcomingEvents(graphics, widget, text, muted, accent);
    }

    protected void DrawTextWidget(Graphics graphics, DesktopWidget widget, RectangleF body, Color text, Color muted)
    {
        using var widgetView = EnterWidgetView();
        var value = DesktopWidgets.NormalizeText(widget.Text);
        var footer = "点击编辑 · 自动保存";
        if (string.IsNullOrWhiteSpace(value)) value = "点击这里，写下第一条记录…";
        if (!_noteTextVisuals.TryGetValue(widget.Id, out var visual)) _noteTextVisuals[widget.Id] = visual = new();
        _widgetScroll[widget.Id] = visual.Draw(graphics, value, NoteFieldRectangle(widget), text,
            _widgetScroll.GetValueOrDefault(widget.Id), insets: new System.Windows.Thickness(8, 6, 8, 6));
        DrawContentIcon(graphics, "content-source", new(body.X, body.Bottom - 17, 14, 14));
        WidgetText(graphics, footer, new(body.X + 20, body.Bottom - 18, body.Width - 20, 18), muted, 11.333f);
    }

    protected void DrawWeatherWidget(Graphics graphics, DesktopWidget widget, RectangleF body, Color text, Color muted)
    {
        using var widgetView = EnterWidgetView();
        var presentation = WeatherPresentation(widget, WidgetCornerRadius);
        body = WeatherRectangle(widget, presentation.Body);
        if (widget.Location is null)
        {
            WidgetText(graphics, "添加天气城市", new(body.X, body.Y + 6, body.Width, 26), text, 15, true, true);
            var button = WeatherEmptyLocationRectangle(widget, WidgetCornerRadius);
            using var path = RoundedRectangle(button, 6);
            using var fill = new SolidBrush(Color.FromArgb(widget.IsLocating ? 12 : 25, text));
            graphics.FillPath(fill, path);
            WidgetText(graphics, widget.IsLocating ? "正在定位…" : "使用当前位置", button, text, 12, centered: true);
            var hint = widget.IsLocating ? "正在获取系统位置…" : widget.LocationAccessDenied ? "定位未授权\n右键打开系统定位设置" :
                !string.IsNullOrEmpty(widget.Error) ? widget.Error : "右键卡片\n搜索并添加城市";
            WidgetText(graphics, hint, new(body.X, body.Y + 78, body.Width, 36), muted, 10.5f, centered: true, multiline: true);
        }
        else if (GetWidgetBounds(widget).Width >= 300)
        {
            DrawWideWeather(graphics, widget, presentation, text, muted);
        }
        else
        {
            var layout = GetWeatherLayout(widget, WidgetCornerRadius);
            if (widget.Weather is { } weather)
            {
                WidgetText(graphics, $"{weather.Temperature:0.#}°", layout.Temperature, text, widget.Cities.Count > 1 && body.Width < 300 ? 38 : 40, true);
                WidgetColorIcons.Draw(graphics, WidgetIconCatalog.GetWeatherName(weather, DateTimeOffset.Now), layout.Icon);
                if (weather.FeelsLike is { } feelsLike)
                    DrawWeatherMetric(graphics, TablerRuntimeIcon.Thermometer, $"{feelsLike:0}°", layout.FeelsLike, muted);
                WidgetText(graphics, $"{weather.Description} · {weather.Minimum:0}° / {weather.Maximum:0}°", layout.Forecast, muted, 12);
                if (weather.Humidity is { } humidity)
                    DrawWeatherMetric(graphics, TablerRuntimeIcon.Droplets, $"{(body.Width >= 300 ? "湿度 " : "")}{humidity:0}%", layout.Humidity, muted);
                if (weather.WindSpeed is { } wind)
                    DrawWeatherMetric(graphics, TablerRuntimeIcon.Wind, $"{(body.Width >= 300 ? "风速 " : "")}{wind:0} km/h", layout.Wind, muted);
            }
            else
            {
                WidgetColorIcons.Draw(graphics, "weather-unavailable", layout.Icon);
                WidgetText(graphics, widget.IsLoading ? "正在获取天气…" : "暂无天气数据",
                    new(body.X, body.Y + 28, Math.Max(0, layout.Icon.Left - body.X - 8), 40), muted);
            }
            var status = widget.IsLocating ? "正在定位当前位置…" : widget.IsLoading ? "正在更新…" : widget.LocationAccessDenied ? "定位未授权，请开启位置服务" :
                !string.IsNullOrEmpty(widget.Error) ? widget.Error : widget.Weather?.AirQuality is { } aqi ? $"AQI {aqi:0}" : "查看天气详情";
            var statusIcon = widget.IsLocating || widget.IsLoading ? TablerRuntimeIcon.RefreshCw : widget.LocationAccessDenied || !string.IsNullOrEmpty(widget.Error)
                ? TablerRuntimeIcon.CircleAlert : widget.Weather?.AirQuality is not null ? TablerRuntimeIcon.Gauge : TablerRuntimeIcon.ChevronRight;
            using var statusPath = RoundedRectangle(layout.Status, 8);
            using var statusFill = new SolidBrush(Color.FromArgb(14, text));
            graphics.FillPath(statusFill, statusPath);
            DrawWeatherMetric(graphics, statusIcon, status,
                new(layout.Status.X + 8, layout.Status.Y, layout.Status.Width - 32, layout.Status.Height), muted);
            TablerRuntimeIcons.Draw(graphics, TablerRuntimeIcon.ChevronRight,
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
