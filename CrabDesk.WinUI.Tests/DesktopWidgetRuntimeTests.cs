using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using CrabDesk.Core;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopWidgetRuntimeTests
{
    [Fact]
    public async Task TranslationUsesItsOwnEndpointCredentialAndModel()
    {
        var (runtime, _) = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        Set(runtime, "_widgetRequests", new Dictionary<Guid, CancellationTokenSource>());
        runtime.State.Settings.AiClassification = new() { BaseUrl = "https://organizer.test/v1", ApiKey = "organizer-key", Model = "organizer" };
        runtime.State.Settings.WidgetTranslation = new() { BaseUrl = "https://translator.test/v1", ApiKey = "translator-key", Model = "translator" };
        var requests = 0;
        using var client = new HttpClient(new TranslationRequestHandler(async request =>
        {
            requests++;
            Assert.Equal("https://translator.test/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("translator-key", request.Headers.Authorization!.Parameter);
            using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("translator", body.RootElement.GetProperty("model").GetString());
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"你好\"}}]}") };
        }));
        using var service = new WidgetTranslationService(client);
        Set(runtime, "_widgetTranslationService", service);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Translation, Text = "Hello" };
        runtime.State.Widgets.Add(widget);
        await runtime.TranslateWidgetAsync(widget);
        Assert.Equal("你好", widget.Translation);
        Assert.Equal(1, requests);
        runtime.State.Settings.WidgetTranslation = new();
        await runtime.TranslateWidgetAsync(widget);
        Assert.Equal(1, requests);
        Assert.Contains("翻译组件设置", widget.Error);
        Assert.Equal("organizer", runtime.State.Settings.AiClassification.Model);
    }

    [Fact]
    public async Task TranslationCredentialsMigrateAndPersistSeparatelyIncludingExplicitClearing()
    {
        var root = Path.Combine(Path.GetTempPath(), "CrabDesk.TranslationSettingsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var (runtime, _) = Fixture();
            using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
            Set(runtime, "_saveTimer", timer);
            var store = new JsonLayoutStore(root);
            Set(runtime, "_layoutStore", store);
            await File.WriteAllTextAsync(store.StatePath, "{\"SchemaVersion\":23,\"Settings\":{\"AiClassification\":{\"BaseUrl\":\"https://legacy.test/v1\",\"Model\":\"legacy-model\"}}}");
            Set(runtime, "<State>k__BackingField", await store.LoadAsync());
            runtime.State.Settings.AiClassification.ApiKey = "legacy-key";
            Call(runtime, "LoadWidgetTranslationApiKey");
            var keyPath = Path.Combine(root, "translation-api-key.dat");
            Assert.Equal("legacy-key", AiApiKeyStore.Load(keyPath));
            Assert.Equal("legacy-key", runtime.State.Settings.WidgetTranslation.ApiKey);
            using var request = new CancellationTokenSource();
            var widget = new DesktopWidget { Kind = DesktopWidgetKind.Translation, IsLoading = true, Text = "Hello", Translation = "已有译文" };
            runtime.State.Widgets.Add(widget);
            Set(runtime, "_widgetRequests", new Dictionary<Guid, CancellationTokenSource> { [widget.Id] = request });
            runtime.ConfigureWidgetTranslation(" https://translator.test/v1 ", "translator-key", " translator ");
            Assert.True(request.IsCancellationRequested);
            Assert.False(widget.IsLoading);
            Assert.Equal("已有译文", widget.Translation);
            Assert.Equal("legacy-model", runtime.State.Settings.AiClassification.Model);
            Assert.Equal("legacy-key", runtime.State.Settings.AiClassification.ApiKey);
            Assert.Equal("translator-key", AiApiKeyStore.Load(keyPath));
            Assert.DoesNotContain("translator-key", await File.ReadAllTextAsync(keyPath));
            await store.SaveAsync(runtime.State);
            Set(runtime, "<State>k__BackingField", await store.LoadAsync());
            runtime.State.Settings.AiClassification.ApiKey = "changed-organizer-key";
            Call(runtime, "LoadWidgetTranslationApiKey");
            Assert.Equal("translator-key", runtime.State.Settings.WidgetTranslation.ApiKey);
            Assert.Equal("translator", runtime.State.Settings.WidgetTranslation.Model);
            runtime.ConfigureWidgetTranslation("https://translator.test/v1", "", "translator");
            Assert.False(File.Exists(keyPath));
            await store.SaveAsync(runtime.State);
            Set(runtime, "<State>k__BackingField", await store.LoadAsync());
            runtime.State.Settings.AiClassification.ApiKey = "changed-organizer-key";
            Call(runtime, "LoadWidgetTranslationApiKey");
            Assert.Empty(runtime.State.Settings.WidgetTranslation.ApiKey);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class TranslationRequestHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    [Fact]
    public void NoteEditsNormalizeAndSaveWithoutChangingOtherWidgetContents()
    {
        var (runtime, _) = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        var note = new DesktopWidget { Kind = DesktopWidgetKind.Note, Text = "old" };
        var translation = new DesktopWidget { Kind = DesktopWidgetKind.Translation, Text = "source" };
        runtime.State.Widgets.AddRange([note, translation]);
        var changes = 0;
        runtime.Changed += (_, _) => changes++;
        var content = "中文\r\n第二行\r" + new string('字', 13000);
        runtime.UpdateWidgetNoteText(note, content);
        Assert.Equal(DesktopWidgets.NormalizeText(content), note.Text);
        Assert.Equal(1, changes);
        runtime.UpdateWidgetNoteText(note, content);
        runtime.UpdateWidgetNoteText(translation, "must not replace source");
        Assert.Equal("source", translation.Text);
        Assert.Equal(1, changes);
        runtime.UpdateWidgetNoteText(note, "");
        Assert.Empty(note.Text);
        runtime.State.Widgets.Remove(note);
        runtime.UpdateWidgetNoteText(note, "late edit");
        Assert.Empty(note.Text);
        Assert.Equal(2, changes);
    }

    [Theory]
    [InlineData(1d)]
    [InlineData(1.5d)]
    public void DesktopNoteEditsStayInCardAndSurviveDismissal(double scale) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture(scale);
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Note, MonitorId = monitor.Id,
            Bounds = new(20, 20, 180, 180), IsLocked = true, Text = "已保存的便签" };
        runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor);
        surface.Location = new(-12000, -12000);
        _ = surface.Handle;
        void Click() => Call(surface, "HandleWidgetClick", widget, new PointF(70, 90));
        Click();
        var editor = (DesktopWidgetTextInput)typeof(DesktopBoxForm).GetField("_noteInput", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        Assert.True(editor.Visible);
        var origin = surface.PointToScreen(new Point((int)(20 * scale), (int)(20 * scale)));
        Assert.InRange(editor.Left, origin.X, origin.X + (int)(180 * scale));
        Assert.InRange(editor.Right, editor.Left + 1, origin.X + (int)(180 * scale));
        Assert.InRange(editor.Top, origin.Y + (int)(28 * scale), origin.Y + (int)(180 * scale));
        Assert.True(editor.InputBox.AcceptsReturn);
        Assert.Equal(0, editor.InputBox.MaxLength);
        Assert.Equal(widget.Text, DesktopWidgets.NormalizeText(editor.InputBox.Text));
        editor.InputBox.Text = "桌面编辑\r\n中文与 emoji 📝";
        Assert.Equal("桌面编辑\n中文与 emoji 📝", widget.Text);
        Assert.False(editor.HandleKey(System.Windows.Input.Key.Enter, System.Windows.Input.ModifierKeys.None));
        Assert.False(editor.HandleKey(System.Windows.Input.Key.ImeProcessed, System.Windows.Input.ModifierKeys.Control));
        Assert.False(editor.HandleKey(System.Windows.Input.Key.Enter, System.Windows.Input.ModifierKeys.Control));
        Assert.True(editor.Visible);
        Assert.True(editor.HandleKey(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None));
        Assert.False(editor.Visible);
        Click();
        Assert.Equal(widget.Text, DesktopWidgets.NormalizeText(editor.InputBox.Text));
        surface.DismissWidgetInputs();
        Assert.False(editor.Visible);
        Assert.Equal("桌面编辑\n中文与 emoji 📝", widget.Text);
        Click();
        widget.Bounds = widget.Bounds with { X = 30 };
        surface.RefreshWidgets();
        Assert.False(editor.Visible);
        Click();
        runtime.State.Widgets.Remove(widget);
        surface.RefreshWidgets();
        Assert.False(editor.Visible);
        Assert.False(runtime.TryConsumeWidgetNavigation(out _));
    });

    [Theory]
    [InlineData(DesktopWidgetKind.Clock)]
    [InlineData(DesktopWidgetKind.Calendar)]
    [InlineData(DesktopWidgetKind.Note)]
    [InlineData(DesktopWidgetKind.Todo)]
    [InlineData(DesktopWidgetKind.Translation)]
    [InlineData(DesktopWidgetKind.Weather)]
    public void ClickingWidgetContentDoesNotNavigateToSettings(DesktopWidgetKind kind) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        var size = DesktopWidgets.DefaultSize(kind);
        var (width, height) = DesktopWidgets.Dimensions(size);
        var widget = new DesktopWidget { Kind = kind, Size = size, MonitorId = monitor.Id, Bounds = new(20, 20, width, height) };
        runtime.State.Widgets.Add(widget);
        DesktopWidget? requestedWeather = null;
        runtime.WidgetWeatherDetailsRequested += requested => requestedWeather = requested;
        using var surface = new DesktopBoxForm(runtime, monitor);
        var point = kind == DesktopWidgetKind.Translation
            ? new PointF(DesktopBoxForm.GetTranslationLayout(widget).Result.X + 8, DesktopBoxForm.GetTranslationLayout(widget).Result.Y + 8)
            : new PointF((float)(20 + width / 2), (float)(20 + (kind == DesktopWidgetKind.Weather ? height - 28 : height / 2)));
        Call(surface, "HandleWidgetClick", widget, point);
        Assert.False(runtime.TryConsumeWidgetNavigation(out _));
        if (kind == DesktopWidgetKind.Weather) Assert.Same(widget, requestedWeather);
    });

    [Fact]
    public void TranslationEditorPreservesSelectionAndUndoWhenReopened() => OnSta(() =>
    {
        using var editor = new DesktopWidgetTextInput("翻译输入验证");
        var content = "第一行\n第二行";
        var bounds = new Rectangle(-12000, -12000, 220, 100);
        void Open() => editor.Present(bounds, content, Color.FromArgb(35, 49, 65), Color.White, Color.CornflowerBlue,
            value => content = value, cornerRadius: 8);
        Open();
        editor.InputBox.Select(0, 3);
        editor.InputBox.SelectedText = "新内容";
        Assert.True(editor.InputBox.CanUndo);
        editor.InputBox.Select(1, 2);
        editor.Dismiss();
        Open();
        Assert.Equal(1, editor.InputBox.SelectionStart); Assert.Equal(2, editor.InputBox.SelectionLength);
        Assert.True(editor.InputBox.CanUndo);
        editor.InputBox.Undo();
        Assert.Equal("第一行\n第二行", content);
    });

    [Fact]
    public void EmptyTranslationDoesNotSubmitOrDismissAndImeConfirmationRemainsNative() => OnSta(() =>
    {
        using var editor = new DesktopWidgetTextInput("翻译输入验证");
        var requests = 0;
        editor.Present(new(-12000, -12000, 220, 100), "  ", Color.Black, Color.White, Color.CornflowerBlue,
            _ => { }, () => requests++, cornerRadius: 8);
        Assert.True(editor.HandleKey(System.Windows.Input.Key.Enter, System.Windows.Input.ModifierKeys.Control));
        Assert.True(editor.Visible); Assert.Equal(0, requests);
        Assert.False(editor.HandleKey(System.Windows.Input.Key.ImeProcessed, System.Windows.Input.ModifierKeys.Control));
        Assert.False(editor.HandleKey(System.Windows.Input.Key.Enter, System.Windows.Input.ModifierKeys.None));
    });

    [Fact]
    public void TranslationSourceAndResultScrollIndependently() => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Translation, MonitorId = monitor.Id, Bounds = new(20, 20, 372, 180) };
        runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor);
        var layout = DesktopBoxForm.GetTranslationLayout(widget);
        Call(surface, "TryScrollWidget", new PointF(layout.Source.X + 12, layout.Source.Y + 12), -120);
        var sourceOffsets = (Dictionary<Guid, float>)typeof(DesktopBoxForm).GetField("_translationSourceScroll", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        var resultOffsets = (Dictionary<Guid, float>)typeof(DesktopBoxForm).GetField("_widgetScroll", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        Assert.Equal(36, sourceOffsets[widget.Id]); Assert.False(resultOffsets.ContainsKey(widget.Id));
        Call(surface, "TryScrollWidget", new PointF(layout.Result.X + 12, layout.Result.Y + 12), -120);
        Assert.Equal(36, resultOffsets[widget.Id]); Assert.Equal(36, sourceOffsets[widget.Id]);
    });

    [Fact]
    public void WidgetCheckboxAndCityArrowStillWorkWithoutOpeningSettings() => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        Set(runtime, "_widgetRequests", new Dictionary<Guid, CancellationTokenSource>());
        Set(runtime, "_weatherRetryAt", new Dictionary<Guid, DateTimeOffset>());
        var todo = new DesktopWidget { Kind = DesktopWidgetKind.Todo, MonitorId = monitor.Id, Bounds = new(20, 20, 180, 180), Tasks = [new() { Text = "任务" }] };
        var weather = new DesktopWidget { Kind = DesktopWidgetKind.Weather, MonitorId = monitor.Id, Bounds = new(220, 20, 180, 180) };
        var first = DesktopWidgets.AddWeatherCity(weather, new("北京", "", "中国", 39.9, 116.4));
        first.Weather = new(18, 12, 22, 2, DateTimeOffset.Now) { Provider = WidgetWeatherService.ProviderName };
        var second = DesktopWidgets.AddWeatherCity(weather, new("上海", "", "中国", 31.2, 121.5));
        second.Weather = first.Weather;
        weather.ActiveCityId = first.Id;
        runtime.State.Widgets.AddRange([todo, weather]);
        using var surface = new DesktopBoxForm(runtime, monitor);
        var todoList = DesktopBoxForm.GetTodoLayout(todo).List;
        Call(surface, "HandleWidgetClick", todo, new PointF(todoList.X + 10, todoList.Y + 14));
        Assert.True(todo.Tasks[0].IsCompleted);
        Call(surface, "HandleWidgetClick", weather, new PointF(385, 184));
        Assert.Equal(second.Id, weather.ActiveCityId);
        Assert.False(runtime.TryConsumeWidgetNavigation(out _));
    });

    [Theory]
    [InlineData(180)]
    [InlineData(372)]
    public void WeatherLocationControlsOpenManagementAndSwitchSavedCities(int width) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        using var menuFont = new Font("Segoe UI", 10);
        Set(runtime, "_menuFont", menuFont);
        Set(runtime, "_lightTrayRenderer", new FluentMenuRenderer(false));
        Set(runtime, "_darkTrayRenderer", new FluentMenuRenderer(true));
        Set(runtime, "_configuredSubmenus", new ConditionalWeakTable<System.Windows.Forms.ToolStripDropDown, object>());
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        Set(runtime, "_widgetRequests", new Dictionary<Guid, CancellationTokenSource>());
        Set(runtime, "_weatherRetryAt", new Dictionary<Guid, DateTimeOffset>());
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Weather, MonitorId = monitor.Id,
            Size = width == 372 ? DesktopWidgetSize.Large : DesktopWidgetSize.Medium, Bounds = new(20, 20, width, 180) };
        var first = DesktopWidgets.AddWeatherCity(widget, new("西安", "陕西", "中国", 34.3, 108.9));
        first.Weather = new(11, 8, 15, 2, DateTimeOffset.Now) { Provider = WidgetWeatherService.ProviderName };
        var second = DesktopWidgets.AddWeatherCity(widget, new("北京", "北京市", "中国", 39.9, 116.4)); second.Weather = first.Weather;
        widget.ActiveCityId = first.Id; runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor);
        surface.Location = new(-12000, -12000); _ = surface.Handle;
        var locations = DesktopBoxForm.WeatherLocationsButtonRectangle(widget);
        Assert.True(DesktopBoxForm.WidgetRectangle(widget).Contains(locations));
        Assert.False(locations.IntersectsWith(DesktopBoxForm.WeatherLocationButtonRectangle(widget)));
        Assert.False(locations.IntersectsWith(new RectangleF(DesktopBoxForm.WidgetRectangle(widget).Right - 32, (float)widget.Bounds.Y, 28, 28)));
        DesktopWidget? requested = null;
        runtime.WidgetWeatherLocationsRequested += selected => requested = selected;
        var point = new PointF(locations.X + 12, locations.Y + 12);
        Call(surface, "HandleWidgetClick", widget, point); Assert.Same(widget, requested);
        Call(surface, "HandleWidgetMouseDown", new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1, (int)point.X, (int)point.Y, 0), point);
        Assert.False((bool)typeof(DesktopBoxForm).GetField("_widgetCanDrag", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!);
        Call(surface, "FinishWidgetPress", false);
        using var cities = (System.Windows.Forms.ContextMenuStrip)Call(surface, "ShowWeatherCityMenu", widget)!;
        var entries = cities.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().ToArray();
        Assert.True(entries.Single(item => item.Text == first.DisplayName).Checked);
        entries.Single(item => item.Text == second.DisplayName).PerformClick();
        Assert.Equal(second.Id, widget.ActiveCityId); Assert.Same(second.Weather, widget.Weather);
        Assert.False(runtime.TryConsumeWidgetNavigation(out _));
        cities.Close();
    });

    [Theory]
    [InlineData(180)]
    [InlineData(372)]
    public void CalendarHeaderButtonsFitAndTodayReturnsToTheCurrentMonth(int width) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Calendar, MonitorId = monitor.Id, Bounds = new(20, 20, width, 180) };
        runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor);
        var header = DesktopBoxForm.GetCalendarHeaderLayout(widget);
        var card = DesktopBoxForm.WidgetRectangle(widget);
        var menu = new RectangleF(card.Right - 32, card.Y, 28, 28);
        var controls = new[] { header.Title, header.Previous, header.Today, header.Next, menu };
        foreach (var control in controls) Assert.True(card.Contains(control));
        for (var i = 0; i < controls.Length; i++)
            for (var j = i + 1; j < controls.Length; j++) Assert.False(controls[i].IntersectsWith(controls[j]));
        void Click(RectangleF rectangle) => Call(surface, "HandleWidgetClick", widget, new PointF(rectangle.X + rectangle.Width / 2, rectangle.Y + rectangle.Height / 2));
        DateOnly Month() => (DateOnly)Call(surface, "WidgetMonth", widget)!;
        var current = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        Click(header.Previous); Assert.Equal(current.AddMonths(-1), Month());
        Click(header.Title); Assert.Equal(current.AddMonths(-1), Month());
        Click(header.Today); Assert.Equal(current, Month());
        Click(header.Next); Assert.Equal(current.AddMonths(1), Month());
        Click(header.Today); Assert.Equal(current, Month());
        var point = new PointF(header.Today.X + 10, header.Today.Y + 10);
        Call(surface, "HandleWidgetMouseDown", new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1, (int)point.X, (int)point.Y, 0), point);
        Call(surface, "HandleWidgetMouseMove", new PointF(point.X + 18, point.Y + 18));
        Assert.Equal(new LayoutRect(20, 20, width, 180), widget.Bounds);
        Assert.False(runtime.TryConsumeWidgetNavigation(out _));
    });

    [Fact]
    public void TodoTaskUpdatesKeepCompletionAndRejectBlankOrRemovedItems()
    {
        var (runtime, _) = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Todo };
        runtime.State.Widgets.Add(widget);
        Assert.Null(runtime.SaveWidgetTask(widget, null, "  \n "));
        var task = runtime.SaveWidgetTask(widget, null, " 第一件\n事情 ")!;
        Assert.Equal("第一件 事情", task.Text);
        task.IsCompleted = true;
        Assert.Same(task, runtime.SaveWidgetTask(widget, task.Id, "修改后的事项"));
        Assert.True(task.IsCompleted); Assert.Single(widget.Tasks);
        Assert.Null(runtime.SaveWidgetTask(widget, Guid.NewGuid(), "不能恢复已删除的事项"));
        runtime.State.Widgets.Remove(widget);
        Assert.Null(runtime.SaveWidgetTask(widget, null, "不能恢复已移除的组件"));
        Assert.Single(widget.Tasks);
    }

    [Theory]
    [InlineData(180)]
    [InlineData(372)]
    public void TodoListAddsEditsAndCancelsInsideTheDesktopCard(int width) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Todo, MonitorId = monitor.Id, Bounds = new(20, 20, width, 180) };
        runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor);
        surface.Location = new(-12000, -12000); _ = surface.Handle;
        var layout = DesktopBoxForm.GetTodoLayout(widget);
        var bounds = DesktopBoxForm.WidgetRectangle(widget);
        Assert.True(bounds.Contains(layout.List)); Assert.True(bounds.Contains(layout.Add));
        Assert.False(layout.List.IntersectsWith(layout.Add));
        Assert.True(layout.Add.Bottom <= bounds.Top + 28);
        Assert.False(layout.Add.IntersectsWith(new RectangleF(bounds.Right - 32, bounds.Y, 28, 28)));
        void Click(RectangleF rectangle) => Call(surface, "HandleWidgetClick", widget, new PointF(rectangle.X + rectangle.Width / 2, rectangle.Y + rectangle.Height / 2));
        DesktopWidgetTextInput Editor() => (DesktopWidgetTextInput)typeof(DesktopBoxForm).GetField("_todoInput", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        Click(layout.Add);
        var editor = Editor();
        Assert.True(editor.Visible); Assert.False(editor.InputBox.AcceptsReturn);
        editor.InputBox.Text = "桌面新建的事项";
        Assert.False(editor.HandleKey(System.Windows.Input.Key.ImeProcessed, System.Windows.Input.ModifierKeys.None));
        Assert.True(editor.HandleKey(System.Windows.Input.Key.Enter, System.Windows.Input.ModifierKeys.None));
        Assert.False(editor.Visible);
        var task = Assert.Single(widget.Tasks); Assert.Equal("桌面新建的事项", task.Text);
        Click(layout.Add); editor.InputBox.Text = "取消添加";
        editor.HandleKey(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None);
        Assert.Single(widget.Tasks);
        var rowText = DesktopBoxForm.TodoEditRectangle(widget, 0);
        var rowHover = new PointF(layout.List.X + 32, layout.List.Y + 14);
        Call(surface, "HandleWidgetMouseMove", rowHover);
        Assert.Equal(task.Id, typeof(DesktopBoxForm).GetField("_hoveredTodoTaskId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface));
        Call(surface, "HandleWidgetMouseMove", new PointF(bounds.Right + 20, bounds.Bottom + 20));
        Assert.Null(typeof(DesktopBoxForm).GetField("_hoveredTodoTaskId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface));
        Click(rowText); editor.InputBox.Text = "取消修改";
        editor.HandleKey(System.Windows.Input.Key.Escape, System.Windows.Input.ModifierKeys.None);
        Assert.Equal("桌面新建的事项", task.Text);
        Click(rowText); editor.InputBox.Text = "修改后的事项";
        editor.HandleKey(System.Windows.Input.Key.Enter, System.Windows.Input.ModifierKeys.None);
        Assert.Equal("修改后的事项", task.Text);
        Call(surface, "HandleWidgetClick", widget, new PointF(layout.List.X + 10, layout.List.Y + 14));
        Assert.True(task.IsCompleted);
        Click(rowText); editor.InputBox.Text = "失焦时保存"; surface.DismissWidgetInputs();
        Assert.Equal("失焦时保存", task.Text); Assert.True(task.IsCompleted);
        for (var i = 0; i < 5; i++) runtime.SaveWidgetTask(widget, null, "更多事项 " + i);
        Click(layout.Add); editor.InputBox.Text = "完整列表末尾添加";
        editor.HandleKey(System.Windows.Input.Key.Enter, System.Windows.Input.ModifierKeys.None);
        Assert.Equal("完整列表末尾添加", widget.Tasks.Last().Text);
        Assert.False(runtime.TryConsumeWidgetNavigation(out _));
    });

    [Fact]
    public void RepeatedAddReusesTheExistingWidgetAndPreservesItsContentsAndPosition()
    {
        var (runtime, _) = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        var note = runtime.AddWidget(DesktopWidgetKind.Note);
        note.Text = "保留这条便签";
        note.Title = "工作记录";
        note.IsLocked = true;
        var bounds = note.Bounds;
        var monitorId = note.MonitorId;
        for (var index = 0; index < 5; index++)
            Assert.Same(note, runtime.AddWidget(DesktopWidgetKind.Note, "another-monitor"));
        Assert.Same(note, Assert.Single(runtime.State.Widgets));
        Assert.Equal("保留这条便签", note.Text);
        Assert.Equal("工作记录", note.Title);
        Assert.True(note.IsLocked);
        Assert.Equal(bounds, note.Bounds);
        Assert.Equal(monitorId, note.MonitorId);
        var clock = runtime.AddWidget(DesktopWidgetKind.Clock);
        Assert.NotSame(note, clock);
        Assert.Equal(2, runtime.State.Widgets.Count);
    }

    [Fact]
    public void RemovingAWidgetAllowsAddingItsKindAgainAndKeepsLegacyDuplicatesUntilRemoved()
    {
        var (runtime, _) = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        Set(runtime, "_widgetRequests", new Dictionary<Guid, CancellationTokenSource>());
        Set(runtime, "_weatherRetryAt", new Dictionary<Guid, DateTimeOffset>());
        var first = runtime.AddWidget(DesktopWidgetKind.Note);
        var legacy = new DesktopWidget { Kind = DesktopWidgetKind.Note, Text = "旧版本的另一条便签" };
        runtime.State.Widgets.Add(legacy);
        Assert.Same(first, runtime.AddWidget(DesktopWidgetKind.Note));
        Assert.Equal(2, runtime.State.Widgets.Count);
        runtime.RemoveWidget(first);
        Assert.Same(legacy, runtime.AddWidget(DesktopWidgetKind.Note));
        Assert.Equal("旧版本的另一条便签", legacy.Text);
        runtime.RemoveWidget(legacy);
        var replacement = runtime.AddWidget(DesktopWidgetKind.Note);
        Assert.NotEqual(first.Id, replacement.Id);
        Assert.NotEqual(legacy.Id, replacement.Id);
        Assert.Same(replacement, Assert.Single(runtime.State.Widgets));
    }

    [Fact]
    public void WidgetOnlyDesktopHasInteractiveCardsAndTransparentGaps() => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        runtime.State.Widgets.Add(new DesktopWidget { Kind = DesktopWidgetKind.Clock, Size = DesktopWidgetSize.Small,
            MonitorId = monitor.Id, Bounds = new(20, 20, 180, 84) });
        using var surface = new DesktopBoxForm(runtime, monitor);
        _ = surface.Handle;
        Assert.True((bool)Call(surface, "UpdateWindowRegion")!);
        Assert.True(surface.ValidateWindowRegion());
        Assert.True(surface.IsPointOverBox(new Point(80, 60)));
        Assert.False(surface.IsPointOverBox(new Point(350, 350)));
        Assert.True((bool)Call(surface, "IsInteractivePointSafe", new PointF(80, 60))!);
        Assert.False((bool)Call(surface, "IsInteractivePointSafe", new PointF(350, 350))!);
        Assert.Single(surface.GetAcrylicRegions());
    });

    [Theory]
    [InlineData(0, 1)]
    [InlineData(8, 1.25)]
    [InlineData(20, 2)]
    public void SixWidgetKindsRenderAtSupportedDpiWithoutPaintingOutsideTheirCards(double radius, double scale) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture(scale);
        runtime.State.Settings.Appearance.CornerRadius = radius;
        var kinds = Enum.GetValues<DesktopWidgetKind>();
        for (var index = 0; index < kinds.Length; index++)
        {
            var widget = new DesktopWidget { Kind = kinds[index], MonitorId = monitor.Id, Size = DesktopWidgets.DefaultSize(kinds[index]), Text = "第一行\n第二行", Translation = "Translated text" };
            var (width, height) = DesktopWidgets.Dimensions(widget.Size);
            widget.Bounds = new(20 + index % 2 * 400, 20 + index / 2 * 200, width, height);
            widget.Tasks.Add(new() { Text = "可勾选的任务", IsCompleted = true });
            widget.Location = new("北京", "", "中国", 39.9, 116.4);
            widget.Weather = new(18, 10, 24, 2, DateTimeOffset.Now);
            runtime.State.Widgets.Add(widget);
        }
        using var surface = new DesktopBoxForm(runtime, monitor);
        using var bitmap = new Bitmap((int)(820 * scale), (int)(620 * scale));
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Magenta);
        graphics.ScaleTransform((float)scale, (float)scale);
        surface.RenderOnIconLayer(graphics, new RectangleF(0, 0, 820, 620));
        foreach (var widget in runtime.State.Widgets)
        {
            Assert.NotEqual(Color.Magenta.ToArgb(), bitmap.GetPixel((int)((widget.Bounds.X + 10) * scale), (int)((widget.Bounds.Y + 40) * scale)).ToArgb());
        }
        Assert.Equal(Color.Magenta.ToArgb(), bitmap.GetPixel((int)(10 * scale), (int)(10 * scale)).ToArgb());
    });

    [Fact]
    public void AcrylicRegionUsesMonitorPixelsAndDpi() => OnSta(() =>
    {
        var (runtime, monitor) = Fixture(1.5);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Clock, MonitorId = monitor.Id, Bounds = new(40, 60, 180, 84) };
        runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor);
        var region = Assert.Single(surface.GetAcrylicRegions());
        Assert.Equal(new RectangleF(60, 90, 270, 126), region.ScreenBounds);
    });

    [Fact]
    public void DesktopWidgetNavigationIsConsumedOnceAndCanReturnToOverview()
    {
        var (runtime, _) = Fixture();
        var id = Guid.NewGuid();
        runtime.OpenWidgetEditor(id);
        Assert.True(runtime.TryConsumeWidgetNavigation(out var target));
        Assert.Equal(id, target);
        Assert.False(runtime.TryConsumeWidgetNavigation(out _));
        runtime.OpenWidgetEditor();
        Assert.True(runtime.TryConsumeWidgetNavigation(out var overview));
        Assert.Null(overview);
    }

    [Fact]
    public async Task SwitchingCitiesCancelsOldRequestWithoutClearingNewLoadingStateOrCache()
    {
        var (runtime, _) = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        Set(runtime, "_widgetRequests", new Dictionary<Guid, CancellationTokenSource>());
        Set(runtime, "_weatherRetryAt", new Dictionary<Guid, DateTimeOffset>());
        var first = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var client = new HttpClient(new DeferredWeatherHandler(() => Interlocked.Increment(ref count) == 1 ? first.Task : second.Task));
        using var service = new WidgetWeatherService(client);
        Set(runtime, "_widgetWeatherService", service);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Weather };
        var beijing = DesktopWidgets.AddWeatherCity(widget, new("北京", "", "中国", 39.9, 116.4) { ProviderKey = "weathercn:101010100" });
        var shanghai = DesktopWidgets.AddWeatherCity(widget, new("上海", "", "中国", 31.2, 121.5) { ProviderKey = "weathercn:101020100" });
        widget.ActiveCityId = beijing.Id;
        beijing.Weather = new(18, 12, 22, 2, DateTimeOffset.Now);
        runtime.State.Widgets.Add(widget);
        var oldRequest = runtime.RefreshWidgetWeatherAsync(widget, force: false);
        Assert.True(widget.IsLoading);
        runtime.SelectWidgetCity(widget, shanghai.Id);
        var newRequest = runtime.RefreshWidgetWeatherAsync(widget);
        first.SetResult(new(System.Net.HttpStatusCode.ServiceUnavailable));
        await oldRequest;
        Assert.True(widget.IsLoading);
        Assert.Empty(widget.Error);
        second.SetResult(new(System.Net.HttpStatusCode.OK) { Content = new StringContent(
            "{\"current\":{\"temperature\":{\"value\":\"24\"},\"weather\":\"0\"},\"forecastDaily\":{\"temperature\":{\"value\":[{\"from\":\"28\",\"to\":\"20\"}]}}}") });
        await newRequest;
        Assert.False(widget.IsLoading);
        Assert.Equal(24, shanghai.Weather!.Temperature);
        Assert.Equal(18, beijing.Weather!.Temperature);
        Assert.Equal(shanghai.Id, widget.ActiveCityId);
    }

    [Fact]
    public async Task LegacyLifeIndicesRefreshOnceBeforeTheNormalWeatherCacheApplies()
    {
        var (runtime, _) = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        Set(runtime, "_widgetRequests", new Dictionary<Guid, CancellationTokenSource>());
        Set(runtime, "_weatherRetryAt", new Dictionary<Guid, DateTimeOffset>());
        var requests = 0;
        using var client = new HttpClient(new DeferredWeatherHandler(() =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""
                {"current":{"temperature":{"value":24},"weather":0},"forecastDaily":{"temperature":{"value":[{"from":28,"to":20}]}},
                "indices":{"indices":[{"type":"uvIndex","value":"6"},{"type":"sports","value":"0"}]}}
                """) });
        }));
        using var service = new WidgetWeatherService(client);
        Set(runtime, "_widgetWeatherService", service);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Weather };
        DesktopWidgets.AddWeatherCity(widget, new("测试", "", "", 1, 2) { ProviderKey = "test" }).Weather =
            new(18, 12, 22, 0, DateTimeOffset.Now) { Provider = WidgetWeatherService.ProviderName, Indices = [new("运动", "适宜")] };
        runtime.State.Widgets.Add(widget);
        await runtime.RefreshWidgetWeatherAsync(widget, force: false);
        Assert.Equal("uvIndex", widget.Weather!.Indices[0].Type);
        var refreshed = widget.Weather;
        await runtime.RefreshWidgetWeatherAsync(widget, force: false);
        Assert.Equal(1, requests); Assert.Same(refreshed, widget.Weather);
    }

    private sealed class DeferredWeatherHandler(Func<Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response();
    }

    [Fact]
    public async Task EditingSourceCancelsTranslationAndDiscardsItsLateFailure()
    {
        var (runtime, _) = Fixture();
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        Set(runtime, "_widgetRequests", new Dictionary<Guid, CancellationTokenSource>());
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new DeferredWeatherHandler(() => response.Task));
        using var service = new WidgetTranslationService(client);
        Set(runtime, "_widgetTranslationService", service);
        runtime.State.Settings.WidgetTranslation.BaseUrl = "https://example.test/v1";
        runtime.State.Settings.WidgetTranslation.Model = "test-model";
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Translation, Text = "old input" };
        runtime.State.Widgets.Add(widget);
        var pending = runtime.TranslateWidgetAsync(widget);
        Assert.True(widget.IsLoading);
        runtime.UpdateWidgetTranslationInput(widget, "新原文\r第二行");
        Assert.False(widget.IsLoading);
        response.SetResult(new(System.Net.HttpStatusCode.ServiceUnavailable));
        await pending;
        Assert.Equal("新原文\n第二行", widget.Text);
        Assert.Empty(widget.Translation);
        Assert.Empty(widget.Error);
        widget.Translation = "previous result";
        runtime.SetWidgetTranslationLanguage(widget, "日语");
        Assert.Equal("日语", widget.TargetLanguage);
        Assert.Empty(widget.Translation);
    }

    [Fact]
    public void TranslationCardFieldsAndButtonsStayInsideTheCardAtSupportedDpi() => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Translation, Size = DesktopWidgetSize.Large,
            MonitorId = monitor.Id, Bounds = new(20, 20, 372, 180) };
        runtime.State.Widgets.Add(widget);
        var layout = DesktopBoxForm.GetTranslationLayout(widget);
        var bounds = DesktopBoxForm.WidgetRectangle(widget);
        foreach (var rectangle in new[] { layout.Source, layout.Result, layout.Translate, layout.Copy, layout.Language, layout.Status })
            Assert.True(bounds.Contains(rectangle));
        Assert.False(layout.Source.IntersectsWith(layout.Result));
        Assert.False(layout.Translate.IntersectsWith(layout.Copy));
        using var surface = new DesktopBoxForm(runtime, monitor);
        Call(surface, "HandleWidgetClick", widget, new PointF(layout.Result.X + 8, layout.Result.Y + 8));
        Assert.False(runtime.TryConsumeWidgetNavigation(out _));
    });

    [Fact]
    public void WidgetDraggingHonorsLockAndKeepsTheCompactHeight() => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Clock, Size = DesktopWidgetSize.Small,
            MonitorId = monitor.Id, IsLocked = true, Bounds = new(20, 20, 180, 84) };
        runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor);
        Set(surface, "_pressedWidget", widget);
        Set(surface, "_widgetPressPoint", new PointF(40, 30));
        Set(surface, "_widgetStartBounds", widget.Bounds);
        Set(surface, "_widgetCanDrag", false);
        Call(surface, "HandleWidgetMouseMove", new PointF(80, 50));
        Assert.Equal(new LayoutRect(20, 20, 180, 84), widget.Bounds);
        widget.IsLocked = false;
        Set(surface, "_widgetCanDrag", true);
        Call(surface, "HandleWidgetMouseMove", new PointF(80, 50));
        Assert.Equal(new LayoutRect(60, 40, 180, 84), widget.Bounds);
        Call(surface, "FinishWidgetPress", false);
        Assert.Equal(84, widget.Bounds.Height);
    });

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void WidgetDragSnapsToNearbyCardsRendersGuidesAndKeepsAlignmentOnRelease(double scale) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture(scale);
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        Set(runtime, "_saveTimer", timer);
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Clock, MonitorId = monitor.Id, Bounds = new(80, 100, 180, 84) };
        var peer = new DesktopWidget { Kind = DesktopWidgetKind.Note, MonitorId = monitor.Id, Bounds = new(100, 230, 180, 84) };
        runtime.State.Widgets.AddRange([widget, peer]);
        using var surface = new DesktopBoxForm(runtime, monitor);
        surface.Location = new(-12000, -12000); _ = surface.Handle;
        Set(surface, "_pressedWidget", widget); Set(surface, "_widgetPressPoint", new PointF(100, 110));
        Set(surface, "_widgetStartBounds", widget.Bounds); Set(surface, "_widgetCanDrag", true);
        IReadOnlyList<DesktopBoxAlignmentGuide> Guides() => (IReadOnlyList<DesktopBoxAlignmentGuide>)
            typeof(DesktopBoxForm).GetField("_boxAlignmentGuides", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        Call(surface, "HandleWidgetMouseMove", new PointF(124, 110));
        Assert.Equal(new LayoutRect(100, 100, 180, 84), widget.Bounds);
        Assert.Contains(Guides(), guide => guide.IsVertical && guide.Position == 100);
        using (var bitmap = new Bitmap(400, 400))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            surface.RenderStaticOnIconLayer(graphics, new(0, 0, 400, 400));
            Assert.Contains(Enumerable.Range(192, 32), y => bitmap.GetPixel(100, y).A > 0);
        }
        if (scale == 1)
        {
            using var acrylic = new Bitmap(400, 400);
            Call(surface, "PaintAcrylicFrame", acrylic);
            Assert.Contains(Enumerable.Range(192, 32), y => acrylic.GetPixel(100, y).A > 0);
        }
        Call(surface, "HandleWidgetMouseMove", new PointF(133, 110));
        Assert.Equal(113, widget.Bounds.X); Assert.Empty(Guides());
        Call(surface, "HandleWidgetMouseMove", new PointF(124, 110));
        Call(surface, "HandleWidgetMouseUp", new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1,
            (int)Math.Round(124 * scale), (int)Math.Round(110 * scale), 0), new PointF(124, 110));
        Assert.Equal(new LayoutRect(100, 100, 180, 84), widget.Bounds);
        Assert.Empty(Guides());
        using var settled = new Bitmap(400, 400);
        using (var graphics = Graphics.FromImage(settled)) surface.RenderStaticOnIconLayer(graphics, new(0, 0, 400, 400));
        Assert.All(Enumerable.Range(192, 32), y => Assert.Equal(0, settled.GetPixel(100, y).A));
    });

    [Fact]
    public void WidgetAlignmentUsesTheVisibleHeightOfCollapsedBoxes() => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Note, MonitorId = monitor.Id, Bounds = new(100, 90, 180, 180) };
        var peer = new DesktopBox { MonitorId = monitor.Id, Bounds = new(100, 230, 244, 288), ExpandOnHover = true };
        runtime.State.Widgets.Add(widget); runtime.State.Boxes.Add(peer);
        using var surface = new DesktopBoxForm(runtime, monitor);
        Call(surface, "ApplyWidgetDragPosition", widget, widget.Bounds, monitor);
        Assert.Equal(230 + peer.Appearance.TitleBarHeight - 180, widget.Bounds.Y);
        Assert.Equal(180, widget.Bounds.Height);
    });

    [Fact]
    public void BoxDraggingCanAlignToANearbyWidget() => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        var box = new DesktopBox { MonitorId = monitor.Id, Bounds = new(104, 100, 244, 180) };
        runtime.State.Boxes.Add(box);
        runtime.State.Widgets.Add(new() { Kind = DesktopWidgetKind.Clock, MonitorId = monitor.Id, Bounds = new(100, 324, 180, 84) });
        using var surface = new DesktopBoxForm(runtime, monitor);
        Call(surface, "ApplyBoxDragPosition", box, box.Bounds, monitor);
        Assert.Equal(100, box.Bounds.X);
        Assert.Equal(100, box.Bounds.Y);
    });

    [Fact]
    public void WidgetAlignmentUsesDistantCardsOnItsMonitorAndKeepsFreeMovementAwayFromAlignmentLines() => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Clock, MonitorId = monitor.Id, Bounds = new(104.2, 100.3, 180, 84) };
        runtime.State.Widgets.AddRange([widget,
            new() { MonitorId = "other-monitor", Bounds = new(100, 230, 180, 84) }]);
        using var surface = new DesktopBoxForm(runtime, monitor);
        var freeBounds = widget.Bounds;
        Call(surface, "ApplyWidgetDragPosition", widget, freeBounds, monitor);
        Assert.Equal(freeBounds, widget.Bounds);
        runtime.State.Widgets.Add(new() { MonitorId = monitor.Id, Bounds = new(100, 500, 180, 84) });
        Call(surface, "ApplyWidgetDragPosition", widget, freeBounds, monitor);
        Assert.Equal(freeBounds with { X = 100 }, widget.Bounds);
        var guides = (IReadOnlyList<DesktopBoxAlignmentGuide>)typeof(DesktopBoxForm)
            .GetField("_boxAlignmentGuides", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        Assert.Equal(584, Assert.Single(guides).End);
        freeBounds = freeBounds with { X = 120.2 };
        Call(surface, "ApplyWidgetDragPosition", widget, freeBounds, monitor);
        Assert.Equal(freeBounds, widget.Bounds);
        Call(surface, "ApplyWidgetDragPosition", widget, new LayoutRect(-5, 690, 180, 84), monitor);
        Assert.Equal(new LayoutRect(0, 616, 180, 84), widget.Bounds);
    });

    [Theory]
    [InlineData("regular")]
    [InlineData("icon")]
    [InlineData("static")]
    [InlineData("acrylic")]
    public void ExpandedBoxKeepsItsContentsAboveCoveredWidgetsInEveryPainter(string painter) => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        runtime.State.Settings.Appearance.AnimationEnabled = false;
        var box = new DesktopBox { MonitorId = monitor.Id, Title = "展开盒子", Bounds = new(100, 100, 260, 240), ExpandOnHover = true };
        box.Appearance.Opacity = 1;
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Note, MonitorId = monitor.Id,
            Bounds = new(130, 170, 180, 84), Text = "组件文字应显示在盒子下方" };
        runtime.State.Boxes.Add(box); runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor);
        Call(surface, "ExpandHoveredBox", box.Id, false);
        Bitmap Render()
        {
            Call(surface, "EnsureGeometry");
            var bitmap = new Bitmap(500, 400);
            if (painter == "acrylic") Call(surface, "PaintAcrylicFrame", bitmap);
            else
            {
                using var graphics = Graphics.FromImage(bitmap);
                if (painter == "regular") Call(surface, "PaintRegularSurface", graphics);
                else if (painter == "icon") surface.RenderOnIconLayer(graphics, new(0, 0, 500, 400));
                else surface.RenderStaticOnIconLayer(graphics, new(0, 0, 500, 400));
            }
            return bitmap;
        }
        using var withWidget = Render();
        runtime.State.Widgets.Clear();
        using var boxOnly = Render();
        for (var y = 180; y < 230; y++)
            for (var x = 140; x < 290; x++)
                Assert.Equal(boxOnly.GetPixel(x, y), withWidget.GetPixel(x, y));
    });

    [Fact]
    public void ExpandedBoxReceivesPointerAndScrollBeforeCoveredWidgetAndCollapseRevealsIt() => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        runtime.State.Settings.Appearance.AnimationEnabled = false;
        var box = new DesktopBox { MonitorId = monitor.Id, Bounds = new(100, 100, 260, 240), ExpandOnHover = true };
        var widget = new DesktopWidget { Kind = DesktopWidgetKind.Note, MonitorId = monitor.Id, Bounds = new(130, 170, 180, 180), Text = "便签" };
        runtime.State.Boxes.Add(box); runtime.State.Widgets.Add(widget);
        using var surface = new DesktopBoxForm(runtime, monitor);
        var point = new PointF(150, 200);
        Assert.Same(widget, Call(surface, "WidgetAt", point));
        Call(surface, "ExpandHoveredBox", box.Id, false);
        Assert.Null(Call(surface, "WidgetAt", point));
        Assert.False((bool)Call(surface, "HandleWidgetMouseMove", point)!);
        Assert.False((bool)Call(surface, "TryScrollWidget", point, -120)!);
        Assert.False((bool)Call(surface, "HandleWidgetMouseDown",
            new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1, 150, 200, 0), point)!);
        Call(surface, "CollapseHoverExpandedBox", box.Id, false);
        Assert.Same(widget, Call(surface, "WidgetAt", point));
    });

    [Fact]
    public void AcrylicForegroundIncludesWidgetsAndKeepsRoundedCornersTransparent() => OnSta(() =>
    {
        var (runtime, monitor) = Fixture();
        runtime.State.Settings.Appearance.CornerRadius = 20;
        runtime.State.Widgets.Add(new DesktopWidget { Kind = DesktopWidgetKind.Clock, Size = DesktopWidgetSize.Small,
            MonitorId = monitor.Id, Bounds = new(20, 20, 180, 84) });
        using var surface = new DesktopBoxForm(runtime, monitor);
        surface.SetAcrylicBackground(true);
        using var bitmap = DesktopLayerBitmapFactory.Create(1000, 700);
        Call(surface, "PaintAcrylicFrame", bitmap);
        Assert.InRange(bitmap.GetPixel(30, 78).A, 170, 200);
        Assert.Equal(0, bitmap.GetPixel(20, 20).A);
        Assert.Equal(0, bitmap.GetPixel(350, 350).A);
    });

    private static (CrabDeskRuntime, MonitorLayout) Fixture(double scale = 1)
    {
        var runtime = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
        var monitor = new MonitorLayout { Id = "widgets-test", DeviceName = "widgets-test", IsPrimary = true, DpiScale = scale,
            Bounds = new(0, 0, 1000, 700), WorkArea = new(0, 0, 1000, 700), PixelBounds = new(0, 0, 1000 * scale, 700 * scale), PixelWorkArea = new(0, 0, 1000 * scale, 700 * scale) };
        Set(runtime, "<State>k__BackingField", new CrabDeskState());
        Set(runtime, "<Items>k__BackingField", Array.Empty<DesktopItemRef>());
        Set(runtime, "<Monitors>k__BackingField", new[] { monitor });
        return (runtime, monitor);
    }

    private static void Set(object instance, string name, object value) => instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);
    private static object? Call(object instance, string name, params object[] args) => instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, args);
    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { error = exception; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
