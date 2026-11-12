using System.Diagnostics;
using CrabDesk.Core;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

public sealed partial class CrabDeskRuntime
{
    private WidgetFileShelfStore? _fileShelfStore;
    private CancellationTokenSource? _fileShelfLifetime;
    private long _fileShelfLastRefresh;
    private bool _fileShelfRefreshing;
    private WidgetFileShelfStore FileShelfStore => _fileShelfStore ??= new(ConfigDirectory);

    private bool HasFileShelf(DesktopWidget widget) => !_disposed && widget.Kind == DesktopWidgetKind.FileShelf && State.Widgets.Contains(widget);

    public void SetFileShelfMode(DesktopWidget widget, WidgetFileShelfMode mode)
    {
        if (!HasFileShelf(widget) || !Enum.IsDefined(mode) || widget.FileShelfMode == mode) return;
        widget.FileShelfMode = mode;
        WidgetChanged(widget);
    }

    public string GetFileShelfPath(DesktopWidget widget, WidgetFileShelfItem item) => FileShelfStore.ResolvePath(widget.Id, item);

    public void SetFileShelfFloating(DesktopWidget widget, bool enabled, bool autoHide)
    {
        if (!HasFileShelf(widget)) return;
        widget.FileShelfFloating.Enabled = enabled;
        widget.FileShelfFloating.AutoHide = autoHide;
        WidgetChanged(widget);
    }

    public void ResetFileShelfFloatingPosition(DesktopWidget widget)
    {
        if (!HasFileShelf(widget)) return;
        widget.FileShelfFloating.Bounds = null;
        widget.FileShelfFloating.DockEdge = FileShelfDockEdge.None;
        WidgetChanged(widget);
    }

    public void SaveFileShelfFloatingPosition(DesktopWidget widget, string monitorId, LayoutRect bounds, FileShelfDockEdge edge)
    {
        if (!HasFileShelf(widget)) return;
        widget.FileShelfFloating.MonitorId = monitorId;
        widget.FileShelfFloating.Bounds = bounds;
        widget.FileShelfFloating.DockEdge = edge;
        widget.FileShelfFloating.Normalize();
        ScheduleSave();
    }

    public async Task PickFileShelfFilesAsync(DesktopWidget widget)
    {
        if (!HasFileShelf(widget) || widget.FileShelfBusy) return;
        using var picker = new Forms.OpenFileDialog { Title = "添加文件到中转站", Multiselect = true, CheckFileExists = true,
            Filter = "所有文件 (*.*)|*.*", RestoreDirectory = true };
        if (picker.ShowDialog() == Forms.DialogResult.OK) await AddFileShelfFilesAsync(widget, picker.FileNames);
    }

    public async Task AddFileShelfFilesAsync(DesktopWidget widget, IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        if (!HasFileShelf(widget) || widget.FileShelfBusy) return;
        var mode = widget.FileShelfMode;
        var sources = paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (sources.Length == 0) return;
        var store = FileShelfStore;
        _fileShelfLifetime ??= new();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_fileShelfLifetime.Token, cancellationToken);
        widget.FileShelfBusy = true;
        widget.FileShelfStatus = mode == WidgetFileShelfMode.Copy ? "正在复制文件…" : "正在加入文件…";
        NotifyFileShelfVisuals(widget);
        var added = 0;
        var failures = new List<string>();
        try
        {
            foreach (var source in sources)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                try
                {
                    if (mode == WidgetFileShelfMode.Reference && widget.FileShelfItems.Any(item => item.Mode == mode &&
                        string.Equals(item.OriginalPath, Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))) continue;
                    var item = await store.AddAsync(widget.Id, source, mode, lifetime.Token);
                    if (!HasFileShelf(widget))
                    {
                        await Task.Run(() => store.Remove(widget.Id, item));
                        break;
                    }
                    widget.FileShelfItems.Add(item);
                    added++;
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                { failures.Add($"{Path.GetFileName(source)}：{exception.Message}"); }
            }
            widget.FileShelfStatus = failures.Count > 0
                ? $"已加入 {added} 个，{failures.Count} 个失败。{failures[0]}"
                : added > 0 ? $"已加入 {added} 个文件" : "这些引用已在暂存列表中";
        }
        catch (OperationCanceledException) { widget.FileShelfStatus = added > 0 ? $"已加入 {added} 个文件，其余操作已取消" : "已取消加入文件"; }
        finally
        {
            widget.FileShelfBusy = false;
            if (HasFileShelf(widget)) WidgetChanged(widget);
        }
    }

    public Task RemoveFileShelfItemAsync(DesktopWidget widget, Guid itemId) =>
        RemoveFileShelfItemsAsync(widget, widget.FileShelfItems.Where(item => item.Id == itemId).ToArray());

    public Task ClearFileShelfAsync(DesktopWidget widget) => RemoveFileShelfItemsAsync(widget, widget.FileShelfItems.ToArray());

    public Task RemoveFileShelfItemsAsync(DesktopWidget widget, IReadOnlyCollection<Guid> ids) =>
        RemoveFileShelfItemsAsync(widget, widget.FileShelfItems.Where(item => ids.Contains(item.Id)).ToArray());

    private async Task RemoveFileShelfItemsAsync(DesktopWidget widget, IReadOnlyList<WidgetFileShelfItem> items)
    {
        if (!HasFileShelf(widget) || widget.FileShelfBusy || items.Count == 0) return;
        var store = FileShelfStore;
        widget.FileShelfBusy = true;
        widget.FileShelfStatus = "正在移除暂存文件…";
        NotifyFileShelfVisuals(widget);
        var failures = new List<string>();
        try
        {
            foreach (var item in items)
            {
                try
                {
                    await Task.Run(() => store.Remove(widget.Id, item));
                    widget.FileShelfItems.Remove(item);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
                { failures.Add($"{item.Name}：{exception.Message}"); }
            }
            widget.FileShelfStatus = failures.Count > 0 ? "部分暂存副本未能移除。" + failures[0] : "已移除暂存条目";
        }
        finally
        {
            widget.FileShelfBusy = false;
            if (HasFileShelf(widget)) WidgetChanged(widget);
        }
    }

    private async Task RemoveFileShelfWidgetAsync(DesktopWidget widget)
    {
        await ClearFileShelfAsync(widget);
        if (HasFileShelf(widget) && !widget.FileShelfBusy && widget.FileShelfItems.Count == 0) RemoveWidget(widget);
    }

    public async Task RefreshFileShelfAvailabilityAsync(bool force = false)
    {
        if (_disposed || _fileShelfRefreshing || IsPaused && !State.Widgets.Any(widget => widget.FileShelfFloating.Enabled)) return;
        var now = Stopwatch.GetTimestamp();
        if (!force && _fileShelfLastRefresh != 0 && Stopwatch.GetElapsedTime(_fileShelfLastRefresh, now).TotalSeconds < 10) return;
        var widgets = State.Widgets.Where(widget => widget.Kind == DesktopWidgetKind.FileShelf && !widget.FileShelfBusy).ToArray();
        if (widgets.Length == 0) return;
        var entries = widgets.SelectMany(widget => widget.FileShelfItems.Select(item => (Widget: widget, Item: item))).ToArray();
        if (entries.Length == 0) return;
        var store = FileShelfStore;
        _fileShelfRefreshing = true;
        _fileShelfLastRefresh = now;
        try
        {
            var states = await Task.Run(() => entries.Select(entry =>
            {
                try { return File.Exists(store.ResolvePath(entry.Widget.Id, entry.Item)); }
                catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException) { return false; }
            }).ToArray());
            if (_disposed) return;
            var changed = new HashSet<DesktopWidget>();
            for (var index = 0; index < entries.Length; index++)
            {
                var (widget, item) = entries[index];
                if (!HasFileShelf(widget) || !widget.FileShelfItems.Contains(item) || item.IsAvailable == states[index]) continue;
                item.IsAvailable = states[index];
                changed.Add(widget);
            }
            if (changed.Count > 0) NotifyFileShelfVisuals(changed.First());
        }
        finally { _fileShelfRefreshing = false; }
    }

    private void NotifyFileShelfVisuals(DesktopWidget widget)
    {
        if (!HasFileShelf(widget)) return;
        _surfaceManager?.RefreshWidgets();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal static string[] FileShelfPaths(DesktopWidget widget, IEnumerable<Guid> selected, Func<WidgetFileShelfItem, string> resolve)
    {
        var ids = selected.ToHashSet();
        var paths = new List<string>();
        foreach (var item in widget.FileShelfItems.Where(item => ids.Contains(item.Id) && item.IsAvailable != false))
        {
            try
            {
                var path = resolve(item);
                if (!string.IsNullOrWhiteSpace(path)) paths.Add(path);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException) { }
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
