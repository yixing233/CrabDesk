using CrabDesk.Core;
using Microsoft.UI.Xaml;

namespace CrabDesk.WinUI.Views;

public sealed partial class WidgetsPage
{
    private void SyncCustomSizeSettings(DesktopWidget widget)
    {
        var supported = DesktopWidgets.SupportsFreeResize(widget.Kind);
        CustomSizeSettings.Visibility = Show(supported);
        if (!supported) return;
        WidgetWidth.Value = widget.Bounds.Width;
        WidgetHeight.Value = widget.Bounds.Height;
    }

    private void ApplyCustomSize_Click(object sender, RoutedEventArgs args)
    {
        if (_loading || _selected is not { } widget || !DesktopWidgets.SupportsFreeResize(widget.Kind)) return;
        _saving = true;
        try { _runtime.ResizeWidget(widget, WidgetWidth.Value, WidgetHeight.Value); Notice.IsOpen = false; }
        catch (InvalidOperationException exception) { Error(exception.Message); return; }
        finally { _saving = false; }
        UpdateOnlineStatus();
        RefreshGallery(changedWidget: widget);
    }
}
