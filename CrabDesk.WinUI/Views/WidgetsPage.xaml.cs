using CrabDesk.Core;
using System.Collections.ObjectModel;
using CrabDesk.Runtime;
using CrabDesk.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace CrabDesk.WinUI.Views;

public sealed partial class WidgetsPage : Page
{
    private readonly CrabDeskRuntime _runtime = App.GetService<CrabDeskRuntime>();
    private DesktopWidget? _selected;
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _citySearch;
    private bool _loading;
    private bool _saving;
    private bool _editing;
    private bool _galleryRefreshQueued;
    private readonly ObservableCollection<WidgetGalleryItem> _galleryItems = [];
    private readonly Dictionary<Guid, WidgetGalleryItem> _galleryCache = [];
    private readonly Dictionary<DesktopWidgetKind, DesktopWidget> _gallerySamples = [];
    private readonly Dictionary<Guid, CheckBox> _taskChecks = [];
    private readonly Dictionary<Guid, TextBlock> _taskDateLabels = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _editorSaveTimer;
    private readonly HashSet<DesktopWidget> _pendingEditorChanges = [];

    public WidgetsPage()
    {
        InitializeComponent();
        _editorSaveTimer = DispatcherQueue.CreateTimer();
        _editorSaveTimer.Interval = TimeSpan.FromMilliseconds(120);
        _editorSaveTimer.IsRepeating = false;
        _editorSaveTimer.Tick += (_, _) => FlushEditorChanges();
        WidgetGallery.ItemsSource = _galleryItems;
        Loaded += (_, _) =>
        {
            _lifetime = new CancellationTokenSource();
            _runtime.Changed += Runtime_Changed;
            _runtime.WidgetMonitorUpdated += Runtime_WidgetMonitorUpdated;
            ReloadInstances();
        };
        Unloaded += (_, _) =>
        {
            FlushEditorChanges();
            _runtime.Changed -= Runtime_Changed;
            _runtime.WidgetMonitorUpdated -= Runtime_WidgetMonitorUpdated;
            _citySearch?.Cancel();
            _lifetime?.Cancel();
            _lifetime?.Dispose();
            _lifetime = null;
        };
    }

    private void Runtime_Changed(object? sender, EventArgs args)
    {
        if (_saving || !IsLoaded || _galleryRefreshQueued) return;
        _galleryRefreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _galleryRefreshQueued = false;
            if (IsLoaded) ReloadInstances();
        })) _galleryRefreshQueued = false;
    }

    private void ReloadInstances()
    {
        LoadGlobalWidgetAppearance();
        LoadWidgetCenterSettings();
        if (_runtime.TryConsumeWidgetNavigation(out var requestedId))
        {
            var requested = _runtime.State.Widgets.FirstOrDefault(w => w.Id == requestedId);
            if (requested is not null) { LoadEditor(requested); return; }
            ShowOverview();
        }
        else if (_selected is not null && !_runtime.State.Widgets.Contains(_selected)) ShowOverview();
        else if (_editing) UpdateOnlineStatus();
        RefreshGallery();
    }

    private WidgetPreviewAppearance PreviewAppearance() => new(_runtime.State.Boxes.FirstOrDefault()?.Appearance
        ?? _runtime.State.Settings.DefaultBoxAppearance, _runtime.State.Settings.Appearance,
        _runtime.IsWidgetCenterDarkTheme ? WidgetColorTheme.Dark : WidgetColorTheme.Light);

    private void RefreshGallery(bool refreshPreviews = true, DesktopWidget? changedWidget = null)
    {
        var appearance = PreviewAppearance();
        var entries = new List<WidgetGalleryItem>();
        var instances = _runtime.State.Widgets.ToLookup(widget => widget.Kind);
        foreach (var kind in Enum.GetValues<DesktopWidgetKind>())
        {
            var added = instances[kind].ToArray();
            var catalog = added.Length == 0;
            if (catalog && !_gallerySamples.ContainsKey(kind)) _gallerySamples[kind] = CreateSample(kind);
            foreach (var widget in catalog ? [_gallerySamples[kind]] : added)
            {
                var caption = catalog ? WidgetGalleryItem.Description(kind)
                    : (kind == DesktopWidgetKind.Network ? DesktopWidgets.NetworkDisplayName(widget.NetworkDisplayMode) + " · " :
                        kind == DesktopWidgetKind.FileShelf ? $"{widget.FileShelfItems.Count} 个文件 · " : "")
                        + DesktopWidgets.SizeLabel(widget) + (widget.Placement == WidgetPlacement.Center ? " · 组件中心" : " · 桌面")
                        + (widget.IsLocked ? " · 已锁定" : "");
                if (!_galleryCache.TryGetValue(widget.Id, out var item))
                    _galleryCache[widget.Id] = item = new WidgetGalleryItem(widget, appearance, catalog, caption, added.Length);
                else
                    item.Refresh(appearance, caption, added.Length, refreshPreviews && (changedWidget is null || widget == changedWidget));
                entries.Add(item);
            }
        }
        var retainedIds = entries.Select(item => item.Widget.Id).ToHashSet();
        foreach (var id in _galleryCache.Keys.Where(id => !retainedIds.Contains(id)).ToArray()) _galleryCache.Remove(id);
        bool? isAdded = WidgetFilter.SelectedIndex switch { 1 => true, 2 => false, _ => null };
        var visible = entries.Where(item => item.Matches(WidgetSearch.Text, isAdded)).ToArray();
        for (var index = 0; index < visible.Length; index++)
        {
            var item = visible[index];
            var previousIndex = _galleryItems.IndexOf(item);
            if (previousIndex < 0) _galleryItems.Insert(index, item);
            else if (previousIndex != index) _galleryItems.Move(previousIndex, index);
        }
        while (_galleryItems.Count > visible.Length) _galleryItems.RemoveAt(_galleryItems.Count - 1);
        UpdateGallerySelection();
        WidgetGallery.Visibility = Show(visible.Length > 0);
        EmptyState.Visibility = Show(visible.Length == 0);
        EmptyStateTitle.Text = !string.IsNullOrWhiteSpace(WidgetSearch.Text) ? "没有匹配的组件"
            : isAdded == true ? "还没有已添加的组件" : "所有组件均已添加";
        GalleryCaption.Text = $"显示 {visible.Length} 项 · 已添加 {_runtime.State.Widgets.Count} 个组件";
        if (refreshPreviews && (changedWidget is null || changedWidget == _selected)) RefreshEditorPreview();
    }

    private void UpdateGallerySelection()
    {
        foreach (var item in _galleryCache.Values) item.IsSelected = !item.IsCatalog && item.Widget == _selected;
        WidgetGallery.SelectedItem = _galleryItems.FirstOrDefault(item => item.IsSelected);
    }

    private static DesktopWidget CreateSample(DesktopWidgetKind kind)
    {
        var sample = new DesktopWidget
        {
            Kind = kind, Size = DesktopWidgets.DefaultSize(kind),
            Text = "今天的灵感\n把重要的事记下来。\n留一点时间给自己。",
            Tasks = [new() { Text = "整理桌面", IsCompleted = true }, new() { Text = "记下今天的计划" }, new() { Text = "读完一本好书" }],
            Translation = "你好，世界。\n让沟通更简单。", TargetLanguage = "简体中文",
            Location = new("北京", "北京市", "中国", 39.9, 116.4), Weather = new(22, 16, 25, 2, DateTimeOffset.Now) { Provider = WidgetWeatherService.ProviderName },
            Performance = kind == DesktopWidgetKind.Performance ? new(DateTimeOffset.Now, 24, 12, 9L << 30, 16L << 30, 8, 2.4 * (1 << 20), 384 * 1024, 240L << 30, 512L << 30) : null,
            Network = kind == DesktopWidgetKind.Network ? new(DateTimeOffset.Now)
            {
                IsConnected = true, InterfaceName = "以太网", NetworkType = "以太网",
                DownloadBytesPerSecond = 2.4 * (1 << 20), UploadBytesPerSecond = 384 * 1024,
                LatencyMilliseconds = 18, LatencyTarget = "1.1.1.1", LatencyStatus = "",
                Processes = new(NetworkProcessStatus.Ready,
                    [new(100, "msedge", 1.6 * (1 << 20), 28 * 1024),
                     new(200, "OneDrive", 640 * 1024, 320 * 1024),
                     new(300, "steam", 180 * 1024, 36 * 1024)])
            } : null,
            PublicIp = kind == DesktopWidgetKind.Network ? "203.0.113.24" : ""
        };
        WidgetAppearance.ApplyNewDefaults(sample);
        if (kind == DesktopWidgetKind.Network)
        {
            var now = DateTimeOffset.Now;
            sample.NetworkHistory.AddRange(Enumerable.Range(0, 61).Select(index =>
                new WidgetNetworkRatePoint(now.AddSeconds(index - 60),
                    (1.4 + Math.Sin(index * .24) * .7 + Math.Sin(index * .73) * .25) * (1 << 20),
                    (240 + Math.Sin(index * .18) * 110 + Math.Cos(index * .51) * 45) * 1024)));
        }
        if (kind == DesktopWidgetKind.Weather && sample.Weather is { } weather)
        {
            var now = DateTimeOffset.Now;
            var nextHour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset).AddHours(1);
            sample.Weather = weather with { AirQuality = 42,
                Hourly = Enumerable.Range(0, 6).Select(index => new WidgetHourlyWeather(nextHour.AddHours(index),
                    22 - index, index < 2 ? "多云" : index < 4 ? "小雨" : "阴", 42)).ToArray() };
        }
        if (kind == DesktopWidgetKind.FileShelf) sample.FileShelfItems = FileShelfSettingsPresentation.SampleItems();
        return sample;
    }

    private void CloseEditor()
    {
        FlushEditorChanges();
        _citySearch?.Cancel();
        CityQuery.IsSuggestionListOpen = false;
        _lifetime?.Cancel(); _lifetime?.Dispose(); _lifetime = null;
        _selected = null;
        _editing = false;
        _appearanceOpen = false;
        UpdatePageNavigation();
        WidgetGallery.SelectedItem = null;
        UpdateGallerySelection();
    }

    private void ShowOverview() => CloseEditor();

    private void ClearGalleryFilters_Click(object sender, RoutedEventArgs args)
    {
        WidgetSearch.Text = string.Empty;
        WidgetFilter.SelectedIndex = 0;
        RefreshGallery(refreshPreviews: false);
    }

    private void WidgetSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    { if (IsLoaded) RefreshGallery(refreshPreviews: false); }

    private void WidgetFilter_SelectionChanged(object sender, SelectionChangedEventArgs args)
    { if (IsLoaded) RefreshGallery(refreshPreviews: false); }

    private void GalleryToolbar_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        var narrow = args.NewSize.Width < 440;
        Grid.SetColumnSpan(WidgetSearch, narrow ? 2 : 1);
        Grid.SetColumn(WidgetFilter, narrow ? 0 : 1);
        Grid.SetRow(WidgetFilter, narrow ? 1 : 0);
        Grid.SetColumnSpan(WidgetFilter, narrow ? 2 : 1);
        WidgetFilter.Width = narrow ? double.NaN : 160;
        WidgetFilter.HorizontalAlignment = HorizontalAlignment.Stretch;
        WidgetFilter.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(0);
    }

    private void WidgetGallery_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not WidgetGalleryItem item) return;
        Try(() => LoadEditor(item.IsCatalog ? _runtime.AddWidget(item.Widget.Kind) : item.Widget));
    }

    private void WidgetGallery_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (WidgetGallery.ItemsPanelRoot is not ItemsWrapGrid panel || args.NewSize.Width <= 0) return;
        var columns = Math.Clamp((int)((args.NewSize.Width + 12) / 212), 1, 3);
        panel.ItemWidth = Math.Floor(args.NewSize.Width / columns);
    }

    private void LoadEditor(DesktopWidget? widget)
    {
        if (widget is null) { ShowOverview(); return; }
        FlushEditorChanges();
        _citySearch?.Cancel();
        _lifetime?.Cancel(); _lifetime?.Dispose();
        _lifetime = new CancellationTokenSource();
        _selected = widget;
        _editing = true;
        _appearanceOpen = false;
        UpdatePageNavigation();
        EditorName.Text = widget.DisplayTitle;
        EditorIcon.AssetName = WidgetIconCatalog.GetName(widget.Kind);
        Notice.IsOpen = false;
        _loading = true;
        try
        {
            TitleInput.Text = widget.Title;
            SizePicker.ItemsSource = DesktopWidgets.SupportedSizes(widget).Select(size => new SizeChoice(size, DesktopWidgets.SizeLabel(widget, size))).ToArray();
            SizePicker.DisplayMemberPath = nameof(SizeChoice.Label);
            SizePicker.SelectedItem = SizePicker.Items.Cast<SizeChoice>().First(s => s.Size == widget.Size);
            MonitorPicker.ItemsSource = _runtime.Monitors.Select((monitor, index) => new MonitorChoice(monitor.Id, $"显示器 {index + 1}" + (monitor.IsPrimary ? "（主显示器）" : ""))).ToArray();
            MonitorPicker.SelectedItem = MonitorPicker.Items.OfType<MonitorChoice>().FirstOrDefault(m => m.Id == widget.MonitorId);
            PositionLock.IsOn = widget.IsLocked;
            Seconds.IsOn = widget.ShowSeconds;
            var translation = _runtime.State.Settings.WidgetTranslation;
            TranslationBaseUrl.Text = translation.BaseUrl;
            TranslationApiKey.Password = translation.ApiKey;
            TranslationModel.Text = translation.Model;
            TranslationPrompt.Text = translation.PromptTemplate;
            TranslationReuseConversation.IsOn = translation.ReuseConversation;
            TranslationConversationIdle.Value = translation.ConversationIdleMinutes;
            TranslationConversationIdle.IsEnabled = translation.ReuseConversation;
            NewTaskInput.Text = string.Empty;
            CityQuery.Text = string.Empty;
            CityQuery.ItemsSource = null;
            CityQuery.IsSuggestionListOpen = false;
            CitySearchStatus.Text = string.Empty;
            LocationSettings.Visibility = Visibility.Collapsed;
            ClockEditor.Visibility = Show(widget.Kind == DesktopWidgetKind.Clock);
            CalendarHelp.Visibility = Show(widget.Kind == DesktopWidgetKind.Calendar);
            NoteHelp.Visibility = Show(widget.Kind == DesktopWidgetKind.Note);
            TodoEditor.Visibility = Show(widget.Kind == DesktopWidgetKind.Todo);
            TranslationEditor.Visibility = Show(widget.Kind == DesktopWidgetKind.Translation);
            WeatherEditor.Visibility = Show(widget.Kind == DesktopWidgetKind.Weather);
            PerformanceEditor.Visibility = Show(widget.Kind == DesktopWidgetKind.Performance);
            NetworkEditor.Visibility = Show(widget.Kind == DesktopWidgetKind.Network);
            FileShelfEditor.Visibility = Show(widget.Kind == DesktopWidgetKind.FileShelf);
            LoadWidgetAppearance(widget);
            LoadWidgetPlacement(widget);
            LoadLyricsSettings(widget);
            LoadWidgetTypography(widget);
            LoadMonitorSettings(widget);
            LoadFileShelfSettings(widget);
        }
        finally { _loading = false; }
        RenderTasks();
        UpdateOnlineStatus();
        EditorScroll.ChangeView(null, 0, null, true);
        RefreshEditorPreview();
        RefreshGallery(refreshPreviews: false);
    }

    private static Visibility Show(bool condition) => condition ? Visibility.Visible : Visibility.Collapsed;
    private void Save(Action<DesktopWidget> change)
    {
        if (_loading || _selected is not { } widget) return;
        change(widget);
        _pendingEditorChanges.Add(widget);
        _editorSaveTimer.Stop();
        _editorSaveTimer.Start();
        EditorName.Text = widget.DisplayTitle;
        UpdatePageNavigation();
        RefreshGallery(refreshPreviews: false);
    }

    private void FlushEditorChanges()
    {
        _editorSaveTimer.Stop();
        if (_pendingEditorChanges.Count == 0) return;
        var widgets = _pendingEditorChanges.ToArray();
        _pendingEditorChanges.Clear();
        _saving = true;
        try
        {
            foreach (var widget in widgets) _runtime.WidgetChanged(widget);
        }
        finally { _saving = false; }
        if (IsLoaded) RefreshGallery(changedWidget: widgets.Length == 1 ? widgets[0] : null);
    }

    private void Error(string message) { Notice.Message = message; Notice.IsOpen = true; }
    private void Try(Action action) { try { action(); } catch (InvalidOperationException exception) { Error(exception.Message); } }

    private void TitleInput_TextChanged(object sender, TextChangedEventArgs args)
    { if (_selected?.Title != TitleInput.Text) Save(w => w.Title = TitleInput.Text); }
    private void PositionLock_Toggled(object sender, RoutedEventArgs args) => Save(w => w.IsLocked = PositionLock.IsOn);
    private void ClockOption_Toggled(object sender, RoutedEventArgs args)
    { if (_selected?.Kind == DesktopWidgetKind.Clock) Save(w => w.ShowSeconds = Seconds.IsOn); }

    private void SizePicker_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_loading && _selected is { } widget && SizePicker.SelectedItem is SizeChoice size) Try(() => _runtime.ResizeWidget(widget, size.Size));
    }

    private void MonitorPicker_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_loading && !_loadingWidgetCenter && _selected is { Placement: WidgetPlacement.Desktop } widget && MonitorPicker.SelectedItem is MonitorChoice monitor) Try(() => _runtime.MoveWidgetToMonitor(widget, monitor.Id));
    }

    private void AddTask_Click(object sender, RoutedEventArgs args) => AddTask();
    private void NewTaskInput_KeyDown(object sender, KeyRoutedEventArgs args)
    { if (args.Key == VirtualKey.Enter) { AddTask(); args.Handled = true; } }
    private void AddTask()
    {
        var text = NewTaskInput.Text.Trim();
        if (_selected?.Kind != DesktopWidgetKind.Todo || text.Length == 0) return;
        Save(w => w.Tasks.Add(new WidgetTodoItem { Text = text }));
        NewTaskInput.Text = string.Empty;
        RenderTasks();
    }

    private void RenderTasks()
    {
        TaskRows.Children.Clear();
        _taskChecks.Clear();
        _taskDateLabels.Clear();
        if (_selected is not { Kind: DesktopWidgetKind.Todo } widget) return;
        foreach (var task in widget.Tasks)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var check = new CheckBox { IsChecked = task.IsCompleted, VerticalAlignment = VerticalAlignment.Center };
            _taskChecks[task.Id] = check;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(check, "完成待办：" + task.Text);
            check.Click += (_, _) => Save(w => { if (w == widget) task.IsCompleted = check.IsChecked == true; });
            var input = new TextBox { Text = task.Text, MaxLength = 500 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(input, "待办内容");
            input.TextChanged += (_, _) => Save(w => { if (w == widget) task.Text = input.Text; });
            var remove = new Button { Content = "删除" };
            remove.Click += (_, _) => { Save(w => { if (w == widget) w.Tasks.Remove(task); }); RenderTasks(); };
            var content = new StackPanel { Spacing = 4 };
            content.Children.Add(input);
            var dateLabel = new TextBlock { Text = task.DateRangeText, FontSize = 12, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SecondaryTextBrush"], TextWrapping = TextWrapping.Wrap,
                Visibility = task.HasDateRange ? Visibility.Visible : Visibility.Collapsed };
            _taskDateLabels[task.Id] = dateLabel; content.Children.Add(dateLabel);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var dates = new Button { Content = new TablerIcon { Icon = TablerIconName.CalendarEvent, FontSize = 14 } };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dates, "设置日期范围：" + task.Text);
            ToolTipService.SetToolTip(dates, "日期范围（可选）");
            dates.Click += (_, _) => _runtime.EditWidgetTaskDates(widget, task.Id);
            actions.Children.Add(dates); actions.Children.Add(remove);
            row.Children.Add(check); row.Children.Add(content); row.Children.Add(actions);
            Grid.SetColumn(content, 1); Grid.SetColumn(actions, 2);
            TaskRows.Children.Add(row);
        }
    }

    private void TranslationSettings_TextChanged(object sender, TextChangedEventArgs args) => SaveTranslationSettings();
    private void TranslationApiKey_PasswordChanged(object sender, RoutedEventArgs args) => SaveTranslationSettings();
    private void TranslationReuseConversation_Toggled(object sender, RoutedEventArgs args)
    {
        TranslationConversationIdle.IsEnabled = TranslationReuseConversation.IsOn;
        SaveTranslationSettings();
    }
    private void TranslationConversationIdle_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => SaveTranslationSettings();
    private void SaveTranslationSettings()
    {
        if (_loading || _selected?.Kind != DesktopWidgetKind.Translation) return;
        _saving = true;
        try
        {
            var idle = TranslationConversationIdle.Value;
            _runtime.ConfigureWidgetTranslation(TranslationBaseUrl.Text, TranslationApiKey.Password, TranslationModel.Text, TranslationPrompt.Text,
                TranslationReuseConversation.IsOn, double.IsFinite(idle) ? (int)Math.Clamp(Math.Round(idle), 1, 1440) : null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { Error("无法保存翻译接口配置，请检查本机配置目录的访问权限。"); }
        finally { _saving = false; }
    }

    private async void ResetTranslationPrompt_Click(object sender, RoutedEventArgs args)
    {
        if (_selected is not { Kind: DesktopWidgetKind.Translation } widget || TranslationPrompt.Text == WidgetTranslationSettings.DefaultPromptTemplate) return;
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "恢复默认提示词？", Content = "当前自定义提示词将替换为默认翻译提示词。",
            PrimaryButtonText = "恢复默认", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !IsLoaded || _selected != widget || !_runtime.State.Widgets.Contains(widget)) return;
        TranslationPrompt.Text = WidgetTranslationSettings.DefaultPromptTemplate;
    }

    private void CityQuery_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _citySearch?.Cancel();
        sender.ItemsSource = null;
        CitySearchStatus.Text = string.Empty;
    }

    private async void CityQuery_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is WeatherLocation location) await AddCityAsync(location);
        else await SearchCitiesAsync(args.QueryText);
    }

    private async Task SearchCitiesAsync(string query)
    {
        if (_selected is not { Kind: DesktopWidgetKind.Weather } widget || _lifetime is null) return;
        _citySearch?.Cancel();
        CityQuery.ItemsSource = null;
        if (string.IsNullOrWhiteSpace(query)) { CitySearchStatus.Text = "输入中文或英文城市名后搜索。"; return; }
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _citySearch = request;
        CitySearchStatus.Text = "正在搜索城市…";
        Notice.IsOpen = false;
        try
        {
            var locations = await _runtime.SearchWidgetCitiesAsync(query, request.Token);
            if (IsLoaded && _selected == widget && !request.IsCancellationRequested)
            {
                CityQuery.ItemsSource = locations;
                CityQuery.IsSuggestionListOpen = locations.Count > 0;
                CitySearchStatus.Text = locations.Count == 0 ? "未找到城市，换个名称试试。" : "选择搜索结果即可添加城市。";
            }
        }
        catch (OperationCanceledException)
        { if (IsLoaded && _selected == widget && !request.IsCancellationRequested) CitySearchStatus.Text = "搜索超时，请重试。"; }
        catch (Exception exception) when (exception is System.Net.Http.HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or System.IO.InvalidDataException or FormatException or KeyNotFoundException)
        { if (IsLoaded && _selected == widget && !request.IsCancellationRequested) CitySearchStatus.Text = "搜索失败，请检查网络后重试。"; }
        finally
        {
            if (_citySearch == request) _citySearch = null;
        }
    }

    private async Task AddCityAsync(WeatherLocation location)
    {
        if (_selected is not { Kind: DesktopWidgetKind.Weather } widget || _lifetime is null) return;
        _citySearch?.Cancel();
        CityQuery.ItemsSource = null;
        CityQuery.Text = string.Empty;
        CityQuery.IsSuggestionListOpen = false;
        CitySearchStatus.Text = string.Empty;
        _runtime.AddWidgetCity(widget, location);
        var work = _runtime.RefreshWidgetWeatherAsync(widget, force: false, cancellationToken: _lifetime.Token);
        UpdateOnlineStatus();
        await work;
        if (IsLoaded && _selected == widget) UpdateOnlineStatus();
    }

    private async void SavedCities_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || _selected is not { Kind: DesktopWidgetKind.Weather } widget ||
            SavedCities.SelectedItem is not WidgetWeatherCity city || widget.ActiveCityId == city.Id || _lifetime is null) return;
        _runtime.SelectWidgetCity(widget, city.Id);
        var work = _runtime.RefreshWidgetWeatherAsync(widget, force: false, cancellationToken: _lifetime.Token);
        UpdateOnlineStatus();
        await work;
        if (IsLoaded && _selected == widget) UpdateOnlineStatus();
    }

    private async void RemoveCity_Click(object sender, RoutedEventArgs args)
    {
        if (_selected is not { Kind: DesktopWidgetKind.Weather, ActiveCity: { } city } widget) return;
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "移除城市？", Content = $"将从这个组件移除“{city.Location.Name}”。",
            PrimaryButtonText = "移除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !IsLoaded || _selected != widget || !_runtime.State.Widgets.Contains(widget)) return;
        _runtime.RemoveWidgetCity(widget, city.Id);
        UpdateOnlineStatus();
        _ = _runtime.RefreshWidgetWeatherAsync(widget, force: false);
    }

    private async void LocateCity_Click(object sender, RoutedEventArgs args)
    {
        if (_selected is not { Kind: DesktopWidgetKind.Weather } widget || _lifetime is null || widget.IsLocating) return;
        Notice.IsOpen = false;
        var work = _runtime.LocateWidgetWeatherAsync(widget, _lifetime.Token);
        UpdateOnlineStatus();
        await work;
        if (IsLoaded && _selected == widget) UpdateOnlineStatus();
    }

    private async void RefreshWeather_Click(object sender, RoutedEventArgs args)
    {
        if (_selected is not { Kind: DesktopWidgetKind.Weather } widget || _lifetime is null) return;
        var work = _runtime.RefreshWidgetWeatherAsync(widget, cancellationToken: _lifetime.Token);
        UpdateOnlineStatus();
        await work;
        if (IsLoaded && _selected == widget) UpdateOnlineStatus();
    }

    private void UpdateOnlineStatus()
    {
        if (_selected is not { } widget) return;
        UpdateMonitorStatus();
        _loading = true;
        PositionLock.IsOn = widget.IsLocked;
        if (widget.Kind == DesktopWidgetKind.Lyrics)
        {
            LoadLyricsOffset(widget);
            LoadLyricsSpacing(widget);
        }
        SyncSizeChoices(widget);
        SyncCustomSizeSettings(widget);
        SyncNetworkDisplaySettings(widget);
        LoadFileShelfSettings(widget);
        RemoveWidgetButton.IsEnabled = widget.Kind != DesktopWidgetKind.FileShelf || !widget.FileShelfBusy;
        MonitorPicker.SelectedItem = MonitorPicker.Items.OfType<MonitorChoice>().FirstOrDefault(m => m.Id == widget.MonitorId);
        foreach (var task in widget.Tasks)
        {
            if (_taskChecks.TryGetValue(task.Id, out var check)) check.IsChecked = task.IsCompleted;
            if (_taskDateLabels.TryGetValue(task.Id, out var label))
            {
                label.Text = task.DateRangeText;
                label.Visibility = task.HasDateRange ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        if (widget.Kind == DesktopWidgetKind.Weather)
        {
            DesktopWidgets.NormalizeWeatherCities(widget);
            var listed = SavedCities.Items.OfType<WidgetWeatherCity>().ToArray();
            if (!listed.SequenceEqual(widget.Cities)) SavedCities.ItemsSource = widget.Cities.ToArray();
            SavedCities.SelectedItem = widget.ActiveCity;
            RemoveCityButton.IsEnabled = widget.ActiveCity is not null;
            LocateCityButton.IsEnabled = !widget.IsLocating;
            LocationSettings.Visibility = Show(widget.LocationAccessDenied);
        }
        _loading = false;
        RefreshWeatherButton.IsEnabled = !widget.IsLoading && !widget.IsLocating && widget.Location is not null;
        CurrentCity.Text = widget.Location?.Name ?? "尚未添加城市";
        var weatherProvider = widget.Weather?.Provider ?? WidgetWeatherService.ProviderName;
        WeatherSource.Content = "数据来自" + weatherProvider;
        WeatherSource.NavigateUri = new Uri(weatherProvider == "Open-Meteo" ? "https://open-meteo.com/" : "https://privacy.mi.com/weather/zh_CN/");
        WeatherDetails.Text = widget.Weather is { } weather
            ? $"{weather.Temperature:0.#}°C · {weather.Description} · {weather.Minimum:0.#}° / {weather.Maximum:0.#}°\n更新于 {weather.FetchedAt:MM/dd HH:mm}"
            : "添加城市后显示天气";
        OperationStatus.Text = widget.IsLocating ? "正在获取 Windows 系统位置…" : widget.IsLoading ? "正在加载…" : widget.Error;
    }

    private async void RemoveWidget_Click(object sender, RoutedEventArgs args)
    {
        if (_selected is not { } widget) return;
        if (widget.Kind == DesktopWidgetKind.FileShelf && !CanChangeFileShelf(widget)) return;
        try
        {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "移除组件？", Content = widget.Kind == DesktopWidgetKind.FileShelf
                ? FileShelfSettingsPresentation.RemovalMessage(widget, clear: false) : $"将从桌面移除“{widget.DisplayTitle}”及其保存的内容。",
                PrimaryButtonText = "移除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (widget.Kind == DesktopWidgetKind.FileShelf && !CanChangeFileShelf(widget)) return;
            _runtime.RemoveWidget(widget);
            ReloadInstances();
        }
        catch (Exception exception) { Error(exception.Message); }
    }

    private sealed record SizeChoice(DesktopWidgetSize Size, string Label);
    private sealed record MonitorChoice(string Id, string Label);
}
