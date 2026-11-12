using System.ComponentModel;
using System.Diagnostics;
using CrabDesk.Core;
using CrabDesk.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace CrabDesk.WinUI.Windows;

public sealed partial class FileShelfWindow
{
    internal const string SourceFormat = "CrabDesk.FileShelfSource";

    private void SyncRows()
    {
        var items = Widget.FileShelfItems;
        var ids = items.Select(item => item.Id).ToHashSet();
        for (var index = _rows.Count - 1; index >= 0; index--) if (!ids.Contains(_rows[index].Item.Id)) _rows.RemoveAt(index);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var row = _rows.FirstOrDefault(entry => entry.Item.Id == item.Id);
            if (row is null)
            {
                row = new FileShelfRow(item); _rows.Insert(index, row);
                _ = LoadIconAsync(row, _lifetime.Token);
            }
            else
            {
                var oldIndex = _rows.IndexOf(row); if (oldIndex != index) _rows.Move(oldIndex, index);
                row.Refresh();
            }
        }
    }

    private async Task LoadIconAsync(FileShelfRow row, CancellationToken cancellation)
    {
        try
        {
            var path = _runtime.GetFileShelfPath(Widget, row.Item);
            if (row.Item.IsAvailable == false) return;
            await _iconGate.WaitAsync(cancellation);
            try
            {
                var icon = await App.GetService<DesktopItemIconSourceFactory>().LoadAsync(path, cancellation);
                if (!_closed && !cancellation.IsCancellationRequested && _rows.Contains(row)) row.Icon = icon;
            }
            finally { _iconGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* The local file glyph remains available when Shell cannot provide an icon. */ }
    }

    private static FileShelfRow? RowAt(object source)
    {
        for (var element = source as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
            if (element is FrameworkElement { DataContext: FileShelfRow row }) return row;
        return null;
    }

    private async Task PickFilesAsync()
    {
        if (Widget.FileShelfBusy || _interactionDepth > 0) return;
        _interactionDepth++;
        try
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, _hwnd);
            var files = await picker.PickMultipleFilesAsync();
            if (!_closed && files.Count > 0) await _runtime.AddFileShelfFilesAsync(Widget, files.Select(file => file.Path));
        }
        catch (Exception exception) { Report("无法添加文件：" + exception.Message); }
        finally { _interactionDepth--; _outsideSince = 0; }
    }

    private async Task OpenAsync(WidgetFileShelfItem item, bool folder)
    {
        _interactionDepth++;
        try
        {
            var path = _runtime.GetFileShelfPath(Widget, item);
            if (!await Task.Run(() => File.Exists(path)))
            {
                item.IsAvailable = false; Report("文件不存在或无法访问"); _runtime.WidgetChanged(Widget); return;
            }
            await Task.Run(() => Process.Start(folder
                ? new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }
                : new ProcessStartInfo(path) { UseShellExecute = true }));
        }
        catch (Exception exception) { Report("无法打开文件：" + exception.Message); }
        finally { _interactionDepth--; _outsideSince = 0; }
    }

    private void ShowFileMenu(UIElement sender, ContextRequestedEventArgs args)
    {
        var row = RowAt(args.OriginalSource);
        if (row is null) return;
        if (!_list.SelectedItems.Contains(row)) { _list.SelectedItems.Clear(); _list.SelectedItems.Add(row); }
        var menu = new MenuFlyout();
        void Add(string title, Action action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = title, IsEnabled = enabled };
            item.Click += (_, _) => action(); menu.Items.Add(item);
        }
        Add("打开文件", () => _ = OpenAsync(row.Item, false));
        Add("打开所在目录", () => _ = OpenAsync(row.Item, true));
        Add("复制选中文件路径", CopyPaths);
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("移除选中条目…", () => _ = RemoveSelectedAsync(), !Widget.FileShelfBusy);
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => { _menuOpen = false; _outsideSince = 0; };
        args.Handled = true;
        if (args.TryGetPosition(_list, out var position)) menu.ShowAt(_list, position); else menu.ShowAt(_list);
    }

    private void CopyPaths()
    {
        try
        {
            var paths = _list.SelectedItems.Cast<FileShelfRow>().Select(row => _runtime.GetFileShelfPath(Widget, row.Item));
            var data = new DataPackage(); data.SetText(string.Join(Environment.NewLine, paths)); Clipboard.SetContent(data);
        }
        catch (Exception exception) { Report("无法复制路径：" + exception.Message); }
    }

    private async Task RemoveSelectedAsync()
    {
        if (Widget.FileShelfBusy || _interactionDepth > 0 || _list.SelectedItems.Count == 0) return;
        var items = _list.SelectedItems.Cast<FileShelfRow>().Select(row => row.Item).ToArray();
        var copies = items.Count(item => item.Mode == WidgetFileShelfMode.Copy);
        _interactionDepth++;
        try
        {
            var dialog = new ContentDialog { XamlRoot = _shell.XamlRoot, Title = "移除中转站文件？",
                Content = $"将移除 {items.Length} 个文件条目。" + (copies > 0 ? $"同时清理中转站管理的 {copies} 份副本。" : "") + "原文件保留。",
                PrimaryButtonText = "移除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && !_closed)
                await _runtime.RemoveFileShelfItemsAsync(Widget, items.Select(item => item.Id).ToArray());
        }
        catch (Exception exception) { Report("无法移除文件：" + exception.Message); }
        finally { _interactionDepth--; _outsideSince = 0; }
    }

    private void StartFileDrag(object sender, DragItemsStartingEventArgs args)
    {
        if (Widget.FileShelfBusy) { args.Cancel = true; return; }
        var paths = new List<string>();
        foreach (var row in args.Items.OfType<FileShelfRow>().Where(row => row.Item.IsAvailable != false))
        {
            try { paths.Add(_runtime.GetFileShelfPath(Widget, row.Item)); }
            catch (Exception exception) when (exception is IOException or ArgumentException) { Report(exception.Message); }
        }
        if (paths.Count == 0) { args.Cancel = true; return; }
        _outgoingDrag = true;
        args.Data.RequestedOperation = DataPackageOperation.Copy;
        args.Data.SetData(SourceFormat, Widget.Id.ToString("D"));
        args.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                var files = new List<IStorageItem>();
                foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try { files.Add(await StorageFile.GetFileFromPathAsync(path).AsTask().ConfigureAwait(false)); }
                    catch (Exception exception) when (exception is FileNotFoundException or UnauthorizedAccessException or ArgumentException or IOException) { }
                }
                request.SetData(files.ToArray());
            }
            catch (Exception exception) { DispatcherQueue.TryEnqueue(() => Report("无法拖出文件：" + exception.Message)); }
            finally { deferral.Complete(); }
        });
    }

    private void NativeDropHover(bool accepted)
    {
        if (_closed) return;
        _incomingDrag = accepted; _outsideSince = 0;
        _dropHint.Visibility = accepted ? Visibility.Visible : Visibility.Collapsed;
        if (!accepted) return;
        Expand();
        _dropText.Text = Widget.FileShelfMode == WidgetFileShelfMode.Copy ? "松开以复制到中转站" : "松开以引用原文件";
    }

    private void NativeDropFiles(string[] paths)
    {
        if (_closed) return;
        _interactionDepth++;
        // Let OLE return to the source first, including desktop sources on this UI thread.
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (!_closed) await _runtime.AddFileShelfFilesAsync(Widget, paths);
            }
            catch (Exception exception) { Report("无法接收文件：" + exception.Message); }
            finally
            {
                _interactionDepth--; _incomingDrag = false; _outsideSince = 0;
                if (!_closed) _dropHint.Visibility = Visibility.Collapsed;
            }
        });
    }
}

public sealed class FileShelfRow(WidgetFileShelfItem item) : INotifyPropertyChanged
{
    public WidgetFileShelfItem Item { get; } = item;
    public string Name => Item.Name;
    public string Detail => WidgetFileShelfPresentation.ItemDetail(Item);
    public double Opacity => Item.IsAvailable == false ? .55 : 1;
    private ImageSource? _icon;
    public ImageSource? Icon { get => _icon; set { _icon = value; Changed(nameof(Icon)); Changed(nameof(FallbackVisibility)); } }
    public Visibility FallbackVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string property) => PropertyChanged?.Invoke(this, new(property));
    public void Refresh() { Changed(nameof(Name)); Changed(nameof(Detail)); Changed(nameof(Opacity)); }
}
