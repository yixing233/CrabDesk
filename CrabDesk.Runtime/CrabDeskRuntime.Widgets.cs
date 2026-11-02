using CrabDesk.Core;

namespace CrabDesk.Runtime;

public sealed partial class CrabDeskRuntime
{
    private readonly WidgetWeatherService _widgetWeatherService = new();
    private readonly WidgetTranslationService _widgetTranslationService = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _widgetRequests = [];
    private readonly Dictionary<Guid, DateTimeOffset> _weatherRetryAt = [];
    public Guid? RequestedWidgetId { get; private set; }
    private bool _widgetNavigationPending;
    public event Action<DesktopWidget>? WidgetWeatherDetailsRequested;
    public event Action<DesktopWidget>? WidgetWeatherLocationsRequested;
    public event Action<DesktopWidget, WidgetTodoItem>? WidgetTodoDatesRequested;

    public void OpenWidgetWeatherLocations(DesktopWidget widget)
    {
        if (!_disposed && widget.Kind == DesktopWidgetKind.Weather && State.Widgets.Contains(widget))
            WidgetWeatherLocationsRequested?.Invoke(widget);
    }

    public void OpenWidgetWeatherDetails(DesktopWidget widget)
    {
        if (!_disposed && widget.Kind == DesktopWidgetKind.Weather && State.Widgets.Contains(widget))
            WidgetWeatherDetailsRequested?.Invoke(widget);
    }

    internal void DismissDesktopWidgetInput() => _surfaceManager?.DismissWidgetInputs();

    private string GetWidgetTranslationApiKeyPath() => Path.Combine(ConfigDirectory, "translation-api-key.dat");

    private void LoadWidgetTranslationApiKey()
    {
        var settings = State.Settings.WidgetTranslation;
        var path = GetWidgetTranslationApiKeyPath();
        if (settings.MigratedFromSharedProfile && !File.Exists(path))
        {
            settings.ApiKey = State.Settings.AiClassification.ApiKey;
            AiApiKeyStore.Save(path, settings.ApiKey);
        }
        else settings.ApiKey = AiApiKeyStore.Load(path);
    }

    public void ConfigureWidgetTranslation(string baseUrl, string apiKey, string model, string? promptTemplate = null,
        bool? reuseConversation = null, int? conversationIdleMinutes = null)
    {
        if (_disposed) return;
        var settings = State.Settings.WidgetTranslation;
        baseUrl = baseUrl?.Trim() ?? string.Empty;
        apiKey ??= string.Empty;
        model = model?.Trim() ?? string.Empty;
        promptTemplate ??= settings.PromptTemplate;
        var reuse = reuseConversation ?? settings.ReuseConversation;
        var idleMinutes = Math.Clamp(conversationIdleMinutes ?? settings.ConversationIdleMinutes, 1, 1440);
        if (settings.BaseUrl == baseUrl && settings.ApiKey == apiKey && settings.Model == model && settings.PromptTemplate == promptTemplate &&
            settings.ReuseConversation == reuse && settings.ConversationIdleMinutes == idleMinutes) return;
        // Persist the credential before updating the live profile, so a failed
        // write cannot silently leave the UI using an unsaved replacement key.
        if (settings.ApiKey != apiKey) AiApiKeyStore.Save(GetWidgetTranslationApiKeyPath(), apiKey);
        foreach (var widget in State.Widgets.Where(widget => widget.Kind == DesktopWidgetKind.Translation))
            CancelWidgetRequest(widget.Id);
        _widgetTranslationService?.ResetConversation();
        settings.BaseUrl = baseUrl;
        settings.ApiKey = apiKey;
        settings.Model = model;
        settings.PromptTemplate = promptTemplate;
        settings.ReuseConversation = reuse;
        settings.ConversationIdleMinutes = idleMinutes;
        _surfaceManager?.RefreshWidgets();
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    internal void UpdateWidgetNoteText(DesktopWidget widget, string text)
    {
        if (_disposed || widget.Kind != DesktopWidgetKind.Note || !State.Widgets.Contains(widget)) return;
        text = DesktopWidgets.NormalizeText(text);
        if (widget.Text == text) return;
        widget.Text = text;
        WidgetChanged(widget);
    }

    public DesktopWidget AddWidget(DesktopWidgetKind kind, string? monitorId = null)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        var existing = State.Widgets.FirstOrDefault(widget => widget.Kind == kind);
        if (existing is not null) return existing;
        var monitor = Monitors.FirstOrDefault(m => m.Id == monitorId) ?? Monitors.FirstOrDefault(m => m.IsPrimary) ?? Monitors.FirstOrDefault();
        if (monitor is null) throw new InvalidOperationException("没有可用的显示器。");
        var size = DesktopWidgets.DefaultSize(kind);
        var (width, height) = DesktopWidgets.Dimensions(size);
        var area = WidgetWorkArea(monitor);
        if (area.Width < width || area.Height < height) throw new InvalidOperationException("当前显示器工作区无法容纳此组件。");
        var obstacles = State.Boxes.Where(b => b.MonitorId == monitor.Id).Select(b => b.Bounds)
            .Concat(State.Widgets.Where(w => w.MonitorId == monitor.Id).Select(w => w.Bounds)).ToArray();
        var bounds = new LayoutRect(area.X + 32, area.Y + 32, width, height).Clamp(area, width, height);
        var found = false;
        for (var y = area.Y + 32; y + height <= area.Y + area.Height && !found; y += 96)
        for (var x = area.X + 32; x + width <= area.X + area.Width && !found; x += 192)
        {
            var candidate = new LayoutRect(x, y, width, height);
            if (obstacles.Any(o => candidate.Intersects(new LayoutRect(o.X - 12, o.Y - 12, o.Width + 24, o.Height + 24)))) continue;
            bounds = candidate;
            found = true;
        }
        var widget = new DesktopWidget { Kind = kind, Size = size, MonitorId = monitor.Id, Bounds = bounds };
        State.Widgets.Add(widget);
        WidgetChanged(widget);
        if (_surfaceManager is not null && (kind is DesktopWidgetKind.Performance or DesktopWidgetKind.Network)) _ = RefreshMonitorWidgetsAsync(true);
        return widget;
    }

    internal static LayoutRect WidgetWorkArea(MonitorLayout monitor) => new(
        monitor.WorkArea.X - monitor.Bounds.X, monitor.WorkArea.Y - monitor.Bounds.Y, monitor.WorkArea.Width, monitor.WorkArea.Height);

    public void WidgetChanged(DesktopWidget widget)
    {
        if (_disposed || !State.Widgets.Contains(widget)) return;
        _surfaceManager?.RefreshWidgets();
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public void RemoveWidget(DesktopWidget widget)
    {
        if (!State.Widgets.Remove(widget)) return;
        CancelPublicIpRequest(widget.Id);
        widget.IsPublicIpLoading = false;
        if (widget.Kind == DesktopWidgetKind.Network) _monitorPingCancellation?.Cancel();
        CancelWidgetRequest(widget.Id);
        if (widget.Kind == DesktopWidgetKind.Translation) _widgetTranslationService?.ResetConversation();
        _weatherRetryAt.Remove(widget.Id);
        if (RequestedWidgetId == widget.Id) RequestedWidgetId = null;
        _surfaceManager?.RefreshWidgets();
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public void ResizeWidget(DesktopWidget widget, DesktopWidgetSize size)
    {
        if (_disposed || !State.Widgets.Contains(widget) || !DesktopWidgets.SupportedSizes(widget).Contains(size)) return;
        if (size == DesktopWidgetSize.Custom) { ResizeWidget(widget, widget.Bounds.Width, widget.Bounds.Height); return; }
        var monitor = Monitors.FirstOrDefault(m => m.Id == widget.MonitorId);
        if (monitor is null) return;
        var (width, height) = DesktopWidgets.Dimensions(size);
        var area = WidgetWorkArea(monitor);
        if (area.Width < width || area.Height < height) throw new InvalidOperationException("当前显示器工作区无法容纳此尺寸。");
        widget.Size = size;
        widget.Bounds = new LayoutRect(widget.Bounds.X, widget.Bounds.Y, width, height).Clamp(area, width, height);
        WidgetChanged(widget);
    }

    public void ResizeWidget(DesktopWidget widget, double width, double height)
    {
        if (_disposed || !State.Widgets.Contains(widget) || !DesktopWidgets.SupportsFreeResize(widget.Kind)) return;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new InvalidOperationException("请输入有效的组件宽度和高度。");
        var monitor = Monitors.FirstOrDefault(m => m.Id == widget.MonitorId);
        if (monitor is null) return;
        var area = WidgetWorkArea(monitor);
        if (area.Width < WidgetSizing.MinimumWidth || area.Height < WidgetSizing.MinimumHeight)
            throw new InvalidOperationException("当前显示器工作区无法容纳此组件。");
        var bounds = new LayoutRect(widget.Bounds.X, widget.Bounds.Y, width, height)
            .Clamp(area, WidgetSizing.MinimumWidth, WidgetSizing.MinimumHeight);
        if (widget.Size == DesktopWidgetSize.Custom && widget.Bounds == bounds) return;
        widget.Size = DesktopWidgetSize.Custom; widget.Bounds = bounds;
        WidgetChanged(widget);
    }

    public void MoveWidgetToMonitor(DesktopWidget widget, string monitorId)
    {
        var monitor = Monitors.FirstOrDefault(m => m.Id == monitorId);
        if (monitor is null) return;
        var area = WidgetWorkArea(monitor);
        if (area.Width < widget.Bounds.Width || area.Height < widget.Bounds.Height) throw new InvalidOperationException("目标显示器工作区无法容纳此组件。");
        widget.MonitorId = monitor.Id;
        widget.Bounds = widget.Bounds.Clamp(area, widget.Bounds.Width, widget.Bounds.Height);
        WidgetChanged(widget);
    }

    public void OpenWidgetEditor(Guid? id = null)
    {
        RequestedWidgetId = id;
        _widgetNavigationPending = true;
        RequestShowSettings("widgets");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool TryConsumeWidgetNavigation(out Guid? widgetId)
    {
        widgetId = RequestedWidgetId;
        var pending = _widgetNavigationPending;
        _widgetNavigationPending = false;
        RequestedWidgetId = null;
        return pending;
    }

    public void ToggleWidgetTask(DesktopWidget widget, Guid taskId)
    {
        if (widget.Tasks.FirstOrDefault(t => t.Id == taskId) is not { } task) return;
        task.IsCompleted = !task.IsCompleted;
        WidgetChanged(widget);
    }

    public WidgetTodoItem? SaveWidgetTask(DesktopWidget widget, Guid? taskId, string text)
    {
        if (_disposed || widget.Kind != DesktopWidgetKind.Todo || !State.Widgets.Contains(widget)) return null;
        text = DesktopWidgets.NormalizeText(text).Replace('\n', ' ').Trim();
        if (text.Length == 0) return null;
        text = text[..Math.Min(500, text.Length)];
        var task = taskId is { } id ? widget.Tasks.FirstOrDefault(item => item.Id == id) : new WidgetTodoItem();
        if (task is null) return null;
        if (taskId is null) widget.Tasks.Add(task);
        else if (task.Text == text) return task;
        task.Text = text;
        WidgetChanged(widget);
        return task;
    }

    public bool RemoveWidgetTask(DesktopWidget widget, Guid taskId)
    {
        if (_disposed || widget.Kind != DesktopWidgetKind.Todo || !State.Widgets.Contains(widget) ||
            widget.Tasks.FirstOrDefault(task => task.Id == taskId) is not { } task) return false;
        widget.Tasks.Remove(task);
        WidgetChanged(widget);
        return true;
    }

    internal void RefreshWidgetPosition(DesktopWidget widget, bool save)
    {
        if (_disposed || !State.Widgets.Contains(widget)) return;
        _surfaceManager?.RefreshWidgets();
        if (save) { Changed?.Invoke(this, EventArgs.Empty); ScheduleSave(); }
    }

    public bool SetWidgetTaskDates(DesktopWidget widget, Guid taskId, DateOnly? start, DateOnly? end)
    {
        if (_disposed || widget.Kind != DesktopWidgetKind.Todo || !State.Widgets.Contains(widget) ||
            widget.Tasks.FirstOrDefault(task => task.Id == taskId) is not { } task) return false;
        if (start is { } first && end is { } last && first > last)
            throw new ArgumentException("截止日期不能早于开始日期。");
        if (task.StartDate == start && task.EndDate == end) return false;
        task.StartDate = start;
        task.EndDate = end;
        WidgetChanged(widget);
        return true;
    }

    public void EditWidgetTaskDates(DesktopWidget widget, Guid taskId)
    {
        if (_disposed || widget.Kind != DesktopWidgetKind.Todo || !State.Widgets.Contains(widget) ||
            widget.Tasks.FirstOrDefault(task => task.Id == taskId) is not { } task) return;
        DismissDesktopWidgetInput();
        WidgetTodoDatesRequested?.Invoke(widget, task);
    }

    public Task<IReadOnlyList<WeatherLocation>> SearchWidgetCitiesAsync(string city, CancellationToken cancellationToken = default) =>
        _widgetWeatherService.SearchAsync(city, cancellationToken);

    public Task<WeatherLocation> ResolveWidgetCityAsync(double latitude, double longitude, CancellationToken cancellationToken = default) =>
        _widgetWeatherService.ResolveAsync(latitude, longitude, cancellationToken);

    public void AddWidgetCity(DesktopWidget widget, WeatherLocation location, bool currentLocation = false)
    {
        if (!State.Widgets.Contains(widget) || widget.Kind != DesktopWidgetKind.Weather) return;
        CancelWidgetRequest(widget.Id);
        DesktopWidgets.AddWeatherCity(widget, location, currentLocation);
        _weatherRetryAt.Remove(widget.Id);
        widget.Error = string.Empty;
        WidgetChanged(widget);
    }

    public void SelectWidgetCity(DesktopWidget widget, Guid cityId)
    {
        if (!State.Widgets.Contains(widget) || !widget.Cities.Any(city => city.Id == cityId) || widget.ActiveCityId == cityId) return;
        CancelWidgetRequest(widget.Id);
        widget.ActiveCityId = cityId;
        _weatherRetryAt.Remove(widget.Id);
        widget.Error = string.Empty;
        WidgetChanged(widget);
    }

    public void RemoveWidgetCity(DesktopWidget widget, Guid cityId)
    {
        if (!State.Widgets.Contains(widget)) return;
        CancelWidgetRequest(widget.Id);
        DesktopWidgets.RemoveWeatherCity(widget, cityId);
        _weatherRetryAt.Remove(widget.Id);
        widget.Error = string.Empty;
        WidgetChanged(widget);
    }

    public async Task RefreshWidgetWeatherAsync(DesktopWidget widget, bool force = true, CancellationToken cancellationToken = default)
    {
        if (_disposed || widget.Kind != DesktopWidgetKind.Weather || widget.Location is null || widget.IsLoading || widget.IsLocating || !State.Widgets.Contains(widget)) return;
        // Older snapshots kept only two indices and have no source types. Refresh
        // them once so the completed life-index cards do not wait for cache expiry.
        if (!force && (widget.Weather is { } cached && cached.Provider == WidgetWeatherService.ProviderName &&
            cached.Indices.All(index => !string.IsNullOrEmpty(index.Type)) && DateTimeOffset.Now - cached.FetchedAt < TimeSpan.FromMinutes(30) ||
            _weatherRetryAt.TryGetValue(widget.Id, out var retry) && DateTimeOffset.Now < retry)) return;
        var location = widget.Location;
        var cityId = widget.ActiveCityId;
        using var cancellation = BeginWidgetRequest(widget, cancellationToken);
        widget.IsLoading = true;
        widget.Error = string.Empty;
        _surfaceManager?.RefreshWidgets();
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            var result = await _widgetWeatherService.GetWeatherAsync(location, cancellation.Token);
            if (!cancellation.IsCancellationRequested && State.Widgets.Contains(widget) && widget.Location == location && widget.ActiveCityId == cityId)
                widget.Weather = result;
        }
        catch (OperationCanceledException)
        {
            if (!cancellation.IsCancellationRequested)
            { widget.Error = "天气请求超时，请稍后重试"; _weatherRetryAt[widget.Id] = DateTimeOffset.Now.AddMinutes(5); }
        }
        catch (Exception exception) when (exception is System.Net.Http.HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or InvalidDataException or FormatException or IndexOutOfRangeException)
        {
            if (!cancellation.IsCancellationRequested)
            {
                widget.Error = "天气更新失败，请稍后重试";
                _weatherRetryAt[widget.Id] = DateTimeOffset.Now.AddMinutes(5);
            }
        }
        finally
        {
            EndWidgetRequest(widget, cancellation);
            if (State.Widgets.Contains(widget)) WidgetChanged(widget);
        }
    }

    public void UpdateWidgetTranslationInput(DesktopWidget widget, string text)
    {
        if (_disposed || widget.Kind != DesktopWidgetKind.Translation || !State.Widgets.Contains(widget)) return;
        text = DesktopWidgets.NormalizeText(text);
        if (text.Length > WidgetTranslationService.MaximumInputLength) text = text[..WidgetTranslationService.MaximumInputLength];
        if (widget.Text == text) return;
        CancelWidgetRequest(widget.Id);
        widget.Text = text;
        widget.Translation = string.Empty;
        widget.Error = string.Empty;
        WidgetChanged(widget);
    }

    public void SetWidgetTranslationLanguage(DesktopWidget widget, string language)
    {
        if (_disposed || widget.Kind != DesktopWidgetKind.Translation || !State.Widgets.Contains(widget) ||
            string.IsNullOrWhiteSpace(language) || widget.TargetLanguage == language) return;
        CancelWidgetRequest(widget.Id);
        _widgetTranslationService?.ResetConversation();
        widget.TargetLanguage = language;
        widget.Translation = string.Empty;
        widget.Error = string.Empty;
        WidgetChanged(widget);
    }

    public async Task TranslateWidgetAsync(DesktopWidget widget, CancellationToken cancellationToken = default)
    {
        if (_disposed || widget.Kind != DesktopWidgetKind.Translation || !State.Widgets.Contains(widget)) return;
        using var cancellation = BeginWidgetRequest(widget, cancellationToken);
        var text = widget.Text;
        var target = widget.TargetLanguage;
        widget.IsLoading = true;
        widget.Error = string.Empty;
        _surfaceManager?.RefreshWidgets();
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            var translated = await _widgetTranslationService.TranslateAsync(State.Settings.WidgetTranslation, text, target, cancellation.Token);
            if (!cancellation.IsCancellationRequested && State.Widgets.Contains(widget) && widget.Text == text && widget.TargetLanguage == target)
                widget.Translation = translated;
        }
        catch (OperationCanceledException)
        {
            if (!cancellation.IsCancellationRequested) widget.Error = "翻译请求超时，请重试";
        }
        catch (Exception exception) when (exception is System.Net.Http.HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or InvalidDataException)
        {
            if (!cancellation.IsCancellationRequested)
                widget.Error = exception is InvalidOperationException or InvalidDataException ? exception.Message : "翻译失败，请检查网络与接口配置";
        }
        finally
        {
            EndWidgetRequest(widget, cancellation);
            if (State.Widgets.Contains(widget)) WidgetChanged(widget);
        }
    }

    private CancellationTokenSource BeginWidgetRequest(DesktopWidget widget, CancellationToken cancellationToken)
    {
        CancelWidgetRequest(widget.Id);
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _widgetRequests[widget.Id] = source;
        return source;
    }

    private void EndWidgetRequest(DesktopWidget widget, CancellationTokenSource source)
    {
        if (_widgetRequests.TryGetValue(widget.Id, out var current) && current == source)
        {
            _widgetRequests.Remove(widget.Id);
            widget.IsLoading = false;
            widget.IsLocating = false;
        }
    }

    public void CancelWidgetRequest(Guid id)
    {
        if (_widgetRequests.Remove(id, out var source)) source.Cancel();
        if (State.Widgets.FirstOrDefault(w => w.Id == id) is { } widget)
        { widget.IsLoading = false; widget.IsLocating = false; widget.LocationAccessDenied = false; }
    }

    private void DisposeWidgets()
    {
        DisposeMonitoring();
        foreach (var id in _widgetRequests.Keys.ToArray()) CancelWidgetRequest(id);
        _widgetWeatherService.Dispose();
        _widgetTranslationService.Dispose();
    }
}
