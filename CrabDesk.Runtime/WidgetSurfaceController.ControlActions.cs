using System.Diagnostics;
using System.Drawing;
using CrabDesk.Core;
using CrabDesk.Native;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

internal abstract partial class WidgetSurfaceController
{
    protected ControlCenterPopup? _controlPopup;
    protected DesktopWidget? _controlPopupWidget;
    protected ControlCenterPresentation.ControlCenterTarget _controlPopupTarget;
    protected LayoutRect _controlPopupBounds;
    protected (Guid Widget, ControlCenterPresentation.ControlCenterTarget Target)? _closedControlPopupClick;

    protected static TablerRuntimeIcon ControlQuickActionIcon(ControlCenterQuickAction action, ControlCenterSnapshot? snapshot) => action switch
    {
        ControlCenterQuickAction.WiFi => snapshot?.WiFiEnabled is true ? TablerRuntimeIcon.Wifi : TablerRuntimeIcon.WifiOff,
        ControlCenterQuickAction.Bluetooth => TablerRuntimeIcon.Bluetooth,
        ControlCenterQuickAction.NightLight => snapshot?.NightLightEnabled is false ? TablerRuntimeIcon.Sun : TablerRuntimeIcon.Moon,
        ControlCenterQuickAction.Display => TablerRuntimeIcon.Monitor,
        ControlCenterQuickAction.Cast => TablerRuntimeIcon.Cast,
        ControlCenterQuickAction.Microphone => TablerRuntimeIcon.Mic,
        ControlCenterQuickAction.ApplicationVolume => TablerRuntimeIcon.SlidersHorizontal,
        ControlCenterQuickAction.ScreenCapture => TablerRuntimeIcon.ScanLine,
        ControlCenterQuickAction.NetworkAdapters => TablerRuntimeIcon.Network,
        _ => TablerRuntimeIcon.Cog
    };

    protected bool HandleControlQuickAction(DesktopWidget widget, PointF point)
    {
        using var widgetView = EnterWidgetView();
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
                ShowControlPopup(widget, target, tile, (dark, background) => new ConnectionDetailsPopup(
                    new ConnectionDetailsController(kind == ControlConnectionKind.WiFi ? new WiFiConnectionService() : new BluetoothConnectionService()),
                    dark, ControlPopupAccent(), _scale, OpenControlSettings,
                    _runtime.State.Settings.Appearance.AnimationEnabled, background));
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
                case ControlCenterQuickAction.NetworkAdapters:
                    ShowControlPopup(widget, ControlCenterPresentation.ControlCenterTarget.NetworkAdapters, tile,
                        (dark, background) => new NetworkAdaptersPopup(new NetworkAdapterControl(), dark,
                            ControlPopupAccent(), _scale, OpenControlSettings,
                            _runtime.State.Settings.Appearance.AnimationEnabled, background));
                    break;
            }
            return true;
        }
        return false;
    }

    protected Color ControlPopupAccent() => ParseOpaqueColor((_runtime.State.Boxes.FirstOrDefault()?.Appearance ??
        _runtime.State.Settings.DefaultBoxAppearance).Accent);

    protected void ShowDisplayControls(DesktopWidget widget, RectangleF anchor) =>
        ShowControlPopup(widget, ControlCenterPresentation.ControlCenterTarget.DisplayControls, anchor, (dark, background) =>
        {
            var popup = new DisplayControlsPopup(new WindowsDisplayControl(), dark, ControlPopupAccent(), _scale, background,
                _runtime.State.Settings.Appearance.AnimationEnabled);
            popup.ExternalActionFailed += ReportControlError;
            return popup;
        });

    protected void ShowQuickActionsEditor(DesktopWidget widget) =>
        ShowControlPopup(widget, ControlCenterPresentation.ControlCenterTarget.None,
            WidgetMenuRectangle(widget, WidgetCornerRadius),
            (dark, background) => new QuickActionsEditorPopup(ControlQuickActions.For(widget), actions => _runtime.SetControlQuickActions(widget, actions),
                dark, ControlPopupAccent(), _scale, background, _runtime.State.Settings.Appearance.AnimationEnabled));

    protected void ShowControlPopup(DesktopWidget widget, ControlCenterPresentation.ControlCenterTarget target,
        RectangleF anchor, Func<bool, Color, ControlCenterPopup> create)
    {
        using var widgetView = EnterWidgetView();
        if (_controlPopup is { IsDisposed: false, Visible: true } && _controlPopupWidget == widget && _controlPopupTarget == target)
        { DismissControlPopup(); return; }
        DismissWidgetInputs();
        var appearance = WidgetBoxAppearance(widget, out var acrylic);
        var background = ParseOpaqueColor(appearance.Background);
        var popup = create(UsesLightText(background), background);
        popup.ApplyPopupMaterial(appearance, _runtime.State.Settings.Appearance, acrylic);
        _controlPopup = popup;
        _controlPopupWidget = widget;
        _controlPopupTarget = target;
        _controlPopupBounds = GetWidgetBounds(widget);
        popup.FormClosed += (_, _) =>
        {
            if (!ReferenceEquals(_controlPopup, popup)) return;
            _controlPopup = null; _controlPopupWidget = null;
            if (_resourcesDisposed || IsDisposed) return;
            RequestVisualLayerRender();
            _ = _runtime.RefreshMonitorWidgetsAsync(force: true);
        };
        popup.ShowAt(this, new Rectangle(WidgetPointToScreen(new(anchor.X, anchor.Y)),
            new Size(ToPixel(anchor.Width), ToPixel(anchor.Height))));
        RequestVisualLayerRender();
    }

    protected void DismissControlPopup()
    {
        using var widgetView = EnterWidgetView();
        _controlPopup?.Dismiss();
        _controlPopup = null; _controlPopupWidget = null;
    }

    protected void RefreshControlPopup()
    {
        using var widgetView = EnterWidgetView();
        if (_controlPopupWidget is { } widget && (!HostHasActiveWidgets || !_runtime.State.Widgets.Contains(widget) ||
            !OwnsActiveWidget(widget) || GetWidgetBounds(widget) != _controlPopupBounds)) DismissControlPopup();
    }

    protected void CaptureControlPopupToggle(Forms.MouseEventArgs args)
    {
        using var widgetView = EnterWidgetView();
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
        if (GetWidgetBounds(widget).Contains(point.X, point.Y) && ControlCenterTargetAt(widget, point) == target)
            _closedControlPopupClick = (widget.Id, target);
    }

    protected bool ConsumeClosedControlPopupClick(DesktopWidget widget, PointF point)
    {
        using var widgetView = EnterWidgetView();
        var closed = _closedControlPopupClick;
        _closedControlPopupClick = null;
        return closed is { } click && click.Widget == widget.Id && click.Target == ControlCenterTargetAt(widget, point);
    }

    protected void OpenControlSettings(string uri) => TryAction(() =>
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose());

    protected void RunControlOperation(Func<ControlOperationResult> operation)
    {
        using var widgetView = EnterWidgetView();
        TryAction(() => { var result = operation(); if (!result.Succeeded) throw new InvalidOperationException(result.Error); });
    }

    protected void ReportControlError(string error)
    {
        using var widgetView = EnterWidgetView();
        if (_resourcesDisposed || IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { BeginInvoke((Action)(() => ReportControlError(error))); return; }
        TryAction(() => throw new InvalidOperationException(error));
    }
}
