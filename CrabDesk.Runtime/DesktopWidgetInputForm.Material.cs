using System.Drawing;
using System.Runtime.InteropServices;
using CrabDesk.Core;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;
using Interop = System.Windows.Interop;
using Media = System.Windows.Media;
using Input = System.Windows.Input;

namespace CrabDesk.Runtime;

internal abstract partial class DesktopWidgetInputForm
{
    private Interop.HwndSource? _popupSource;
    private PopupKeyboardFilter? _popupKeyboard;
    private PopupMaterial? _popupMaterial;

    private sealed record PopupMaterial(Color Background, byte Alpha, bool Acrylic, bool Dark, double Radius);

    internal void ApplyPopupMaterial(BoxAppearance appearance, GlobalAppearanceSettings settings, bool? acrylic = null)
    {
        if (_entranceSurface is not System.Windows.Controls.Border surface) return;
        var background = BoxBorderStyle.TryParseColor(appearance.Background, out var parsed) ? parsed : BackColor;
        var dark = background.R * .299 + background.G * .587 + background.B * .114 < 150;
        var useAcrylic = acrylic ?? settings.UseAcrylicBoxes;
        var material = new PopupMaterial(background,
            (byte)Math.Round(255 * appearance.ResolveBackgroundOpacity(useAcrylic)),
            useAcrylic, dark, settings.ResolveWidgetCornerRadius() > 0 ? 8 : 0);
        if (_popupMaterial == material) return;
        _popupMaterial = material;
        BackColor = background;
        surface.Background = MaterialBrush(background, material.Alpha);
        surface.BorderBrush = null;
        surface.BorderThickness = new Wpf.Thickness(0);
        surface.CornerRadius = new Wpf.CornerRadius(material.Radius);
        if (IsHandleCreated) ApplyNativePopupMaterial();
        Invalidate();
    }

    private void CreatePopupContent()
    {
        if (_entranceSurface is not { } surface) return;
        // ElementHost fills transparent pixels with its WinForms parent's color.
        // A per-pixel child HWND preserves the tint over the native desktop acrylic.
        _popupSource = new Interop.HwndSource(new Interop.HwndSourceParameters(Text, ClientSize.Width, ClientSize.Height)
        {
            ParentWindow = Handle,
            WindowStyle = 0x50000000, // WS_CHILD | WS_VISIBLE
            UsesPerPixelTransparency = true,
            TreatAsInputRoot = true
        });
        _popupSource.CompositionTarget.BackgroundColor = Media.Colors.Transparent;
        Input.KeyboardNavigation.SetTabNavigation(surface, Input.KeyboardNavigationMode.Cycle);
        _popupSource.RootVisual = surface;
        _popupKeyboard = new PopupKeyboardFilter(_popupSource);
        Forms.Application.AddMessageFilter(_popupKeyboard);
        ApplyNativePopupMaterial();
    }

    private void ReleasePopupContent()
    {
        if (_popupKeyboard is { } keyboard) Forms.Application.RemoveMessageFilter(keyboard);
        _popupKeyboard = null;
        if (_popupSource is { } source)
        {
            source.RootVisual = null;
            source.Dispose();
            _popupSource = null;
        }
    }

    protected void ApplyNativePopupMaterial()
    {
        var border = unchecked((int)0xFFFFFFFE);
        _ = DwmSetPopupAttribute(Handle, 34, ref border, sizeof(int));
        if (_popupMaterial is not { } material) return;
        var margins = new PopupMargins(-1, -1, -1, -1);
        _ = DwmExtendPopupFrame(Handle, ref margins);
        var dark = material.Dark ? 1 : 0;
        _ = DwmSetPopupAttribute(Handle, 20, ref dark, sizeof(int));
        var corners = material.Radius > 0 ? 2 : 1;
        _ = DwmSetPopupAttribute(Handle, 33, ref corners, sizeof(int));
        // DWMSBT_TRANSIENTWINDOW supplies the desktop acrylic used by flyouts.
        var backdrop = material.Acrylic ? 3 : 1;
        var result = DwmSetPopupAttribute(Handle, 38, ref backdrop, sizeof(int));
        if (result < 0 && material.Acrylic && _entranceSurface is System.Windows.Controls.Border surface)
            surface.Background = MaterialBrush(material.Background, 255);
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        if (_popupSource is { } source)
            SetWindowPos(source.Handle, IntPtr.Zero, 0, 0, ClientSize.Width, ClientSize.Height, 0x0014);
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (_popupSource is { } source)
        {
            // A layered WPF child caches screen coordinates for presentation.
            // Moving its parent alone leaves that cache at the previous frame.
            SetWindowPos(source.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0015);
        }
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (_entranceSurface is { IsKeyboardFocusWithin: false } surface)
            surface.MoveFocus(new Input.TraversalRequest(Input.FocusNavigationDirection.First));
    }

    protected override void OnPaintBackground(Forms.PaintEventArgs e)
    {
        if (_popupMaterial is not null) e.Graphics.Clear(Color.Black);
        else base.OnPaintBackground(e);
    }

    private static Media.SolidColorBrush MaterialBrush(Color color, byte alpha)
    {
        var brush = new Media.SolidColorBrush(Media.Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private sealed class PopupKeyboardFilter(Interop.HwndSource source) : Forms.IMessageFilter
    {
        public bool PreFilterMessage(ref Forms.Message message)
        {
            if (message.HWnd != source.Handle || message.Msg is < 0x0100 or > 0x0109) return false;
            // Let WPF handle text, Tab navigation and Escape before WinForms
            // preprocesses messages for the containing Form.
            var input = new Interop.MSG
            {
                hwnd = message.HWnd, message = message.Msg, wParam = message.WParam, lParam = message.LParam
            };
            return Interop.ComponentDispatcher.RaiseThreadMessage(ref input);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PopupMargins(int left, int right, int top, int bottom)
    {
        public int Left = left, Right = right, Top = top, Bottom = bottom;
    }

    [DllImport("dwmapi.dll", EntryPoint = "DwmExtendFrameIntoClientArea")]
    private static extern int DwmExtendPopupFrame(IntPtr hwnd, ref PopupMargins margins);
}
