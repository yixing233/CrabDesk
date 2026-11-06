using System.Diagnostics;
using System.Drawing;
using CrabDesk.Core;
using CrabDesk.Native;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

internal sealed partial class DesktopBoxForm
{
    private ControlCenterPopup? _controlPopup;
    private DesktopWidget? _controlPopupWidget;
    private ControlCenterPresentation.ControlCenterTarget _controlPopupTarget;
    private LayoutRect _controlPopupBounds;
    private (Guid Widget, ControlCenterPresentation.ControlCenterTarget Target)? _closedControlPopupClick;

    private static LucideRuntimeIcon ControlQuickActionIcon(ControlCenterQuickAction action, ControlCenterSnapshot? snapshot) => action switch
    {
        ControlCenterQuickAction.WiFi => snapshot?.WiFiEnabled is true ? LucideRuntimeIcon.Wifi : LucideRuntimeIcon.WifiOff,
        ControlCenterQuickAction.Bluetooth => LucideRuntimeIcon.Bluetooth,
        ControlCenterQuickAction.NightLight => LucideRuntimeIcon.Moon,
        ControlCenterQuickAction.Display => LucideRuntimeIcon.Monitor,
        ControlCenterQuickAction.Cast => LucideRuntimeIcon.Cast,
        ControlCenterQuickAction.Microphone => LucideRuntimeIcon.Mic,
        ControlCenterQuickAction.ApplicationVolume => LucideRuntimeIcon.SlidersHorizontal,
        ControlCenterQuickAction.ScreenCapture => LucideRuntimeIcon.ScanLine,
        _ => LucideRuntimeIcon.Cog
    };

    private bool HandleControlQuickAction(DesktopWidget widget, PointF point)
    {
        var body = MonitorBody(widget);
        var actions = ControlQuickActions.For(widget);
        for (var index = 0; index < actions.Count; index++)
        {
            var local = ControlCenterPresentation.QuickAction(body, index, actions.Count);
            var tile = MonitorRectangle(widget, local);
            if (!tile.Contains(point)) continue;
            var action = actions[index];
            if (ControlQuickActions.HasDetails(action) && MonitorRectangle(widget, ControlCenterPresentation.QuickActionDetails(local)).Contains(point))
            {
                var kind = action == ControlCenterQuickAction.WiFi ? ControlConnectionKind.WiFi : ControlConnectionKind.Bluetooth;
                var target = kind == ControlConnectionKind.WiFi ? ControlCenterPresentation.ControlCenterTarget.WiFiDetails :
                    ControlCenterPresentation.ControlCenterTarget.BluetoothDetails;
                ShowControlPopup(widget, target, tile, () => new ConnectionDetailsPopup(
                    new ConnectionDetailsController(kind == ControlConnectionKind.WiFi ? new WiFiConnectionService() : new BluetoothConnectionService()),
                    _runtime.IsDarkTheme, ControlPopupAccent(), _scale, OpenControlSettings));
            }
            else if (ControlQuickActions.Toggle(action) is { } toggle)
            {
                if (!ControlCenterPresentation.ToggleAvailable(toggle, widget.Controls)) return true;
                var enabled = toggle switch
                {
                    ControlCenterToggle.WiFi => widget.Controls?.WiFiEnabled is true,
                    ControlCenterToggle.Bluetooth => widget.Controls?.BluetoothEnabled is true,
                    _ => widget.Controls?.NightLightEnabled is true
                };
                _ = _runtime.ToggleControlAsync(widget, toggle, !enabled);
            }
            else switch (action)
            {
                case ControlCenterQuickAction.Display: ShowDisplayControls(widget, tile); break;
                case ControlCenterQuickAction.Cast: RunControlOperation(new WindowsDisplayControl().OpenCast); break;
                case ControlCenterQuickAction.Microphone: ShowMicrophone(widget); break;
                case ControlCenterQuickAction.ApplicationVolume: ShowControlCenterMixer(widget); break;
                case ControlCenterQuickAction.ScreenCapture: OpenControlSettings("ms-screenclip:"); break;
                case ControlCenterQuickAction.Settings: OpenControlSettings("ms-settings:"); break;
            }
            return true;
        }
        return false;
    }

    private Color ControlPopupAccent() => ParseOpaqueColor((_runtime.State.Boxes.FirstOrDefault()?.Appearance ??
        _runtime.State.Settings.DefaultBoxAppearance).Accent);

    private void ShowDisplayControls(DesktopWidget widget, RectangleF anchor) =>
        ShowControlPopup(widget, ControlCenterPresentation.ControlCenterTarget.DisplayControls, anchor, () =>
        {
            var popup = new DisplayControlsPopup(new WindowsDisplayControl(), _runtime.IsDarkTheme, ControlPopupAccent(), _scale);
            popup.ExternalActionFailed += ReportControlError;
            return popup;
        });

    private void ShowQuickActionsEditor(DesktopWidget widget) =>
        ShowControlPopup(widget, ControlCenterPresentation.ControlCenterTarget.None,
            WidgetMenuRectangle(widget, _runtime.State.Settings.Appearance.CornerRadius),
            () => new QuickActionsEditorPopup(ControlQuickActions.For(widget), actions => _runtime.SetControlQuickActions(widget, actions),
                _runtime.IsDarkTheme, ControlPopupAccent(), _scale));

    private void ShowControlPopup(DesktopWidget widget, ControlCenterPresentation.ControlCenterTarget target,
        RectangleF anchor, Func<ControlCenterPopup> create)
    {
        if (_controlPopup is { IsDisposed: false, Visible: true } && _controlPopupWidget == widget && _controlPopupTarget == target)
        { DismissControlPopup(); return; }
        DismissWidgetInputs();
        var popup = create();
        _controlPopup = popup;
        _controlPopupWidget = widget;
        _controlPopupTarget = target;
        _controlPopupBounds = widget.Bounds;
        popup.FormClosed += (_, _) =>
        {
            if (!ReferenceEquals(_controlPopup, popup)) return;
            _controlPopup = null; _controlPopupWidget = null;
            if (_resourcesDisposed || IsDisposed) return;
            RequestVisualLayerRender();
            _ = _runtime.RefreshMonitorWidgetsAsync(force: true);
        };
        popup.ShowAt(this, new Rectangle(PointToScreen(new Point(ToPixel(anchor.X), ToPixel(anchor.Y))),
            new Size(ToPixel(anchor.Width), ToPixel(anchor.Height))));
        RequestVisualLayerRender();
    }

    private void DismissControlPopup()
    {
        _controlPopup?.Dismiss();
        _controlPopup = null; _controlPopupWidget = null;
    }

    private void RefreshControlPopup()
    {
        if (_controlPopupWidget is { } widget && (_runtime.IsPaused || !_runtime.State.Widgets.Contains(widget) ||
            widget.MonitorId != _monitor.Id || widget.Bounds != _controlPopupBounds)) DismissControlPopup();
    }

    private void CaptureControlPopupToggle(Forms.MouseEventArgs args)
    {
        _closedControlPopupClick = null;
        if (args.Button != Forms.MouseButtons.Left) return;
        var (widget, target) = _controlPopup is { IsDisposed: false, Visible: true }
            ? (_controlPopupWidget, _controlPopupTarget)
            : _microphonePopup is { IsDisposed: false, Visible: true }
                ? (_microphoneWidget, ControlCenterPresentation.ControlCenterTarget.Microphone)
                : _controlMixerPopup is { IsDisposed: false, Visible: true }
                    ? (_controlMixerWidget, ControlCenterPresentation.ControlCenterTarget.VolumeMixer)
                    : (null, ControlCenterPresentation.ControlCenterTarget.None);
        if (widget is null || target == ControlCenterPresentation.ControlCenterTarget.None) return;
        var point = ToDip(args.Location);
        if (widget.Bounds.Contains(point.X, point.Y) && ControlCenterTargetAt(widget, point) == target)
            _closedControlPopupClick = (widget.Id, target);
    }

    private bool ConsumeClosedControlPopupClick(DesktopWidget widget, PointF point)
    {
        var closed = _closedControlPopupClick;
        _closedControlPopupClick = null;
        return closed is { } click && click.Widget == widget.Id && click.Target == ControlCenterTargetAt(widget, point);
    }

    private void OpenControlSettings(string uri) => TryAction(() =>
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose());

    private void RunControlOperation(Func<ControlOperationResult> operation)
    {
        TryAction(() => { var result = operation(); if (!result.Succeeded) throw new InvalidOperationException(result.Error); });
    }

    private void ReportControlError(string error)
    {
        if (_resourcesDisposed || IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { BeginInvoke((Action)(() => ReportControlError(error))); return; }
        TryAction(() => throw new InvalidOperationException(error));
    }
}
