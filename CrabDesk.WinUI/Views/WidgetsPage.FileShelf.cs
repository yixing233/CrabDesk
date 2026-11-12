using CrabDesk.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CrabDesk.WinUI.Views;

public sealed partial class WidgetsPage
{
    private bool _fileShelfConfirmationOpen;

    private void LoadFileShelfSettings(DesktopWidget widget)
    {
        if (widget.Kind != DesktopWidgetKind.FileShelf) return;
        _loadingFileShelfFloating = true;
        try
        {
            FileShelfFloatingToggle.IsOn = widget.FileShelfFloating.Enabled;
            FileShelfAutoHideToggle.IsOn = widget.FileShelfFloating.AutoHide;
            FileShelfAutoHideToggle.IsEnabled = FileShelfResetPosition.IsEnabled = widget.FileShelfFloating.Enabled;
        }
        finally { _loadingFileShelfFloating = false; }
        FileShelfModePicker.SelectedIndex = widget.FileShelfMode == WidgetFileShelfMode.Copy ? 1 : 0;
        FileShelfModePicker.IsEnabled = !widget.FileShelfBusy;
        FileShelfModeHelp.Text = FileShelfSettingsPresentation.ModeExplanation(widget.FileShelfMode);
        FileShelfSummary.Text = FileShelfSettingsPresentation.Summary(widget);
        FileShelfAddButton.IsEnabled = !widget.FileShelfBusy;
        FileShelfClearButton.IsEnabled = !widget.FileShelfBusy && widget.FileShelfItems.Count > 0;
        FileShelfStatus.Text = widget.FileShelfBusy && string.IsNullOrEmpty(widget.FileShelfStatus)
            ? "正在处理文件…" : widget.FileShelfStatus;
        FileShelfStatus.Visibility = Show(!string.IsNullOrEmpty(FileShelfStatus.Text));
    }

    private bool CanChangeFileShelf(DesktopWidget widget)
    {
        if (!widget.FileShelfBusy) return true;
        Error("文件中转站正在处理文件，请等待完成后再操作。");
        return false;
    }

    private void FileShelfModePicker_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || _selected is not { Kind: DesktopWidgetKind.FileShelf } widget || FileShelfModePicker.SelectedIndex < 0) return;
        var mode = FileShelfModePicker.SelectedIndex == 1 ? WidgetFileShelfMode.Copy : WidgetFileShelfMode.Reference;
        if (mode == widget.FileShelfMode || !CanChangeFileShelf(widget)) return;
        _saving = true;
        try { _runtime.SetFileShelfMode(widget, mode); Notice.IsOpen = false; }
        catch (Exception exception) { Error(exception.Message); }
        finally { _saving = false; }
        UpdateOnlineStatus();
        RefreshGallery(changedWidget: widget);
    }

    private async void FileShelfAdd_Click(object sender, RoutedEventArgs args)
    {
        if (_selected is not { Kind: DesktopWidgetKind.FileShelf } widget || !CanChangeFileShelf(widget)) return;
        Notice.IsOpen = false;
        try { await _runtime.PickFileShelfFilesAsync(widget); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Error(exception.Message); }
        finally { RefreshFileShelfAfterOperation(widget); }
    }

    private async void FileShelfClear_Click(object sender, RoutedEventArgs args)
    {
        if (_selected is not { Kind: DesktopWidgetKind.FileShelf } widget || widget.FileShelfItems.Count == 0 ||
            _fileShelfConfirmationOpen || !CanChangeFileShelf(widget)) return;
        _fileShelfConfirmationOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "清空中转站？",
                Content = FileShelfSettingsPresentation.RemovalMessage(widget, clear: true),
                PrimaryButtonText = "清空", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || !IsLoaded || _selected != widget ||
                !CanChangeFileShelf(widget)) return;
            Notice.IsOpen = false;
            await _runtime.ClearFileShelfAsync(widget);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Error(exception.Message); }
        finally
        {
            _fileShelfConfirmationOpen = false;
            RefreshFileShelfAfterOperation(widget);
        }
    }

    private void RefreshFileShelfAfterOperation(DesktopWidget widget)
    {
        if (!IsLoaded || _selected != widget) return;
        UpdateOnlineStatus();
        RefreshGallery(changedWidget: widget);
    }

    private bool _loadingFileShelfFloating;
    private void FileShelfFloating_Toggled(object sender, RoutedEventArgs args)
    {
        if (_loading || _loadingFileShelfFloating || _selected is not { Kind: DesktopWidgetKind.FileShelf } widget) return;
        _runtime.SetFileShelfFloating(widget, FileShelfFloatingToggle.IsOn, FileShelfAutoHideToggle.IsOn);
    }

    private void FileShelfResetPosition_Click(object sender, RoutedEventArgs args)
    {
        if (_selected is { Kind: DesktopWidgetKind.FileShelf } widget) _runtime.ResetFileShelfFloatingPosition(widget);
    }
}

internal static class FileShelfSettingsPresentation
{
    internal static string ModeExplanation(WidgetFileShelfMode mode) => mode == WidgetFileShelfMode.Copy
        ? "将文件复制到中转站，保存独立副本。打开和拖出使用副本；清空或移除时删除中转站管理的副本，原文件保留。"
        : "记录原文件的路径。打开和拖出使用原文件；原文件移动或删除后，条目会显示不可用。";

    internal static string Summary(DesktopWidget widget)
    {
        var copies = widget.FileShelfItems.Count(item => item.Mode == WidgetFileShelfMode.Copy);
        var missing = widget.FileShelfItems.Count(item => item.IsAvailable is false);
        return $"共 {widget.FileShelfItems.Count} 个文件 · {widget.FileShelfItems.Count - copies} 个引用 · {copies} 个副本" +
            (missing > 0 ? $"\n{missing} 个文件不可用" : "");
    }

    internal static string RemovalMessage(DesktopWidget widget, bool clear)
    {
        var copies = widget.FileShelfItems.Count(item => item.Mode == WidgetFileShelfMode.Copy);
        var action = clear ? $"将清空“{widget.DisplayTitle}”中的 {widget.FileShelfItems.Count} 个文件条目。"
            : $"将从桌面移除“{widget.DisplayTitle}”及其 {widget.FileShelfItems.Count} 个文件条目。";
        return action + (copies > 0 ? $"\n同时删除中转站管理的 {copies} 个副本。原文件保留。"
            : "\n原文件保留。");
    }

    internal static List<WidgetFileShelfItem> SampleItems() =>
    [
        new() { OriginalPath = @"C:\Users\Example\Documents\项目计划.pdf", Mode = WidgetFileShelfMode.Reference, IsAvailable = true },
        new() { OriginalPath = @"C:\Users\Example\Pictures\设计草图.png", Mode = WidgetFileShelfMode.Copy, IsAvailable = true },
        new() { OriginalPath = @"C:\Users\Example\Downloads\素材包.zip", Mode = WidgetFileShelfMode.Reference, IsAvailable = true }
    ];
}
