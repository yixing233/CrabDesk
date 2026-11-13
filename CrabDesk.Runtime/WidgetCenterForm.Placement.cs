using System.Drawing;
using System.Runtime.InteropServices;
using CrabDesk.Core;
using CrabDesk.Native;
using Forms = System.Windows.Forms;

namespace CrabDesk.Runtime;

internal sealed partial class WidgetCenterForm
{
    protected override bool ForceSolidAppearance => true;
    protected override Forms.CreateParams CreateParams
    {
        get { var value = base.CreateParams; value.ExStyle |= 0x80; return value; } // WS_EX_TOOLWINDOW; normal activation retained
    }

    internal static Rectangle ResolvePixelBounds(WidgetCenterSettings settings, MonitorLayout monitor)
    {
        var scale = Math.Max(.1, monitor.DpiScale);
        var area = monitor.PixelWorkArea;
        var margin = Math.Min(12 * scale, Math.Max(0, Math.Min(area.Width, area.Height) / 4));
        var width = (int)Math.Round(Math.Min(PanelWidthDip * scale, area.Width - margin * 2));
        var height = (int)Math.Round(area.Height - margin * 2);
        // Keep the rounded card close to the right edge without touching it.
        return new((int)Math.Round(area.X + area.Width - margin - width), (int)Math.Round(area.Y + margin),
            Math.Max(1, width), Math.Max(1, height));
    }

    private void RestorePlacement()
    {
        var settings = _runtime.State.Settings.WidgetCenter;
        _monitor = _runtime.Monitors.FirstOrDefault(m => m.PixelWorkArea.Contains(Forms.Cursor.Position.X, Forms.Cursor.Position.Y)) ??
            _runtime.Monitors.FirstOrDefault(m => m.IsPrimary) ?? _monitor;
        _scale = Math.Max(.1, _monitor.DpiScale);
        _picker.UpdateScale(_scale);
        Bounds = ResolvePixelBounds(settings, _monitor);
    }
    private void QueuePlacementSave()
    {
        if (!Visible || _presenting || _panelMotion.Enabled) return;
        _placementTimer.Stop(); _placementTimer.Start();
    }
    private void SavePlacement()
    {
        if (!_ready || IsDisposed) return;
        var monitor = _runtime.Monitors.FirstOrDefault(m => m.PixelBounds.Contains(Left + Width / 2d, Top + Height / 2d)) ?? _monitor;
        var scale = Math.Max(.1, monitor.DpiScale);
        _runtime.SaveWidgetCenterPlacement(monitor.Id, Left / scale, Top / scale, Width / scale, Height / scale);
    }

    private void ApplyCenterMaterial()
    {
        if (!IsHandleCreated || IsDisposed) return;
        var dark = _runtime.IsWidgetCenterDarkTheme ? 1 : 0;
        _ = DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        // The panel surface owns the outline and transparent corners throughout its motion.
        var corners = 1; // DWMWCP_DONOTROUND
        _ = DwmSetWindowAttribute(Handle, 33, ref corners, sizeof(int));
        var framePolicy = 1; // DWMNCRP_DISABLED
        _ = DwmSetWindowAttribute(Handle, 2, ref framePolicy, sizeof(int));
        if (_panelComposition is null && CanUsePanelAcrylic)
        {
            try { _panelComposition = new WidgetCenterComposition(Handle); }
            catch (Exception error) { DiagnosticLog.Error("Widget center acrylic initialization failed", error); }
        }
        _panelComposition?.SetAcrylic(CanUsePanelAcrylic);
        Invalidate();
    }

    internal static bool ShouldDismissForActivation(bool visible, bool protectedInteraction, bool belongsToHost, uint foregroundProcessId, uint hostProcessId) =>
        visible && !protectedInteraction && !belongsToHost && foregroundProcessId != 0 && foregroundProcessId != hostProcessId;

    private void CheckOutsideActivation()
    {
        if (!Visible || IsDisposed) { _outsideTimer.Stop(); return; }
        if (HasProtectedInteraction || _acceptingDrop || _panelMotion.Enabled) return;
        var foreground = GetForegroundWindow();
        var rootOwner = GetAncestor(foreground, 3); // GA_ROOTOWNER includes owned native pickers/popups
        _ = GetWindowThreadProcessId(foreground, out var processId);
        var belongs = foreground == Handle || rootOwner == Handle || OwnedForms.Any(form => form.IsHandleCreated &&
            (form.Handle == foreground || form.Handle == rootOwner));
        if (ShouldDismissForActivation(Visible, false, belongs, processId, (uint)Environment.ProcessId))
        { _outsideTimer.Stop(); _runtime.HideWidgetCenterOnDeactivate(); }
        // Same-process WinUI settings/date/weather windows are intentional
        // interaction. Continue checking so switching to another app still hides.
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
