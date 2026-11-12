using System.Drawing;
using System.Runtime.InteropServices;
using CrabDesk.Core;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Interop = System.Windows.Interop;
using Media = System.Windows.Media;

namespace CrabDesk.Runtime;

/// <summary>Preserves desktop acrylic beneath a WPF card hosted by a menu.</summary>
internal sealed class PopupCardMaterial : IDisposable, Forms.IMessageFilter
{
    private readonly Forms.ToolStripDropDown _menu;
    private readonly Forms.Integration.ElementHost _host;
    private readonly Wpf.UIElement _content;
    private readonly Controls.Border _surface;
    private readonly bool _animate;
    private readonly bool _acrylic;
    private readonly Media.Color _tint;
    private readonly int _dark, _corners;
    private readonly Forms.Timer _timer = new() { Interval = DesktopAnimationFrameClock.IntervalMilliseconds };
    private Interop.HwndSource? _source;
    private System.Windows.Threading.DispatcherOperation? _reveal;
    private Point _target;
    private long _started;
    private int _distance, _frames;
    private bool _disposed;
    private Region? _emptyHostRegion;

    internal PopupCardMaterial(Forms.ToolStripDropDown menu, Forms.Integration.ElementHost host,
        BoxAppearance appearance, GlobalAppearanceSettings settings, bool animationsEnabled, bool? acrylic = null)
    {
        _menu = menu; _host = host; _content = host.Child;
        if (menu is FluentContextMenuStrip root) { root.CardOwnsPresentation = true; root.PrepareCardPainting(); }
        else if (menu is FluentToolStripDropDownMenu dropdown) dropdown.PrepareCardPainting();
        var background = BoxBorderStyle.TryParseColor(appearance.Background, out var parsed) ? parsed : host.BackColor;
        _dark = background.R * .299 + background.G * .587 + background.B * .114 < 150 ? 1 : 0;
        _corners = settings.ResolveWidgetCornerRadius() > 0 ? 2 : 1;
        _acrylic = (acrylic ?? settings.UseAcrylicBoxes) && !Wpf.SystemParameters.HighContrast;
        _animate = animationsEnabled && Wpf.SystemParameters.ClientAreaAnimation;
        _tint = Media.Color.FromArgb((byte)Math.Round(255 * appearance.ResolveBackgroundOpacity(_acrylic)),
            background.R, background.G, background.B);
        _surface = new Controls.Border { Background = new Media.SolidColorBrush(_tint),
            CornerRadius = new Wpf.CornerRadius(_corners == 2 ? 8 : 0) };
        _menu.Opening += Opening;
        _menu.Opened += Opened;
        _menu.Closed += Closed;
        _menu.LocationChanged += AlignContent;
        _menu.SizeChanged += AlignContent;
        _host.Disposed += HostDisposed;
        _timer.Tick += Animate;
    }

    private void Opening(object? sender, System.ComponentModel.CancelEventArgs args)
    {
        Cloak(true);
        _menu.Opacity = _animate ? .01 : 1;
    }

    private void Opened(object? sender, EventArgs args)
    {
        CrabDesk.Native.DesktopPopupWindow.Track(_menu.Handle);
        var border = unchecked((int)0xFFFFFFFE);
        _ = DwmSetWindowAttribute(_menu.Handle, 34, ref border, 4);
        var dark = _dark;
        _ = DwmSetWindowAttribute(_menu.Handle, 20, ref dark, 4);
        var corners = _corners;
        _ = DwmSetWindowAttribute(_menu.Handle, 33, ref corners, 4);
        var margins = new Margins(-1, -1, -1, -1);
        _ = DwmExtendFrameIntoClientArea(_menu.Handle, ref margins);
        var backdrop = _acrylic ? 3 : 1;
        var supported = DwmSetWindowAttribute(_menu.Handle, 38, ref backdrop, 4) >= 0;
        var tint = _tint;
        if (!supported || Wpf.SystemParameters.HighContrast) tint.A = 255;
        _surface.Background = Wpf.SystemParameters.HighContrast ? Wpf.SystemColors.WindowBrush : new Media.SolidColorBrush(tint);
        _menu.Renderer = new CardRenderer();
        _host.Child = null;
        _emptyHostRegion = new Region();
        _emptyHostRegion.MakeEmpty();
        // Hiding the host makes ToolStrip remove its row and collapse the menu.
        // Clip its empty HWND instead, preserving layout and the native backdrop.
        _host.Region = _emptyHostRegion;
        _surface.Child = _content;
        _source = new Interop.HwndSource(new Interop.HwndSourceParameters(_menu.Text, _menu.ClientSize.Width, _menu.ClientSize.Height)
        {
            ParentWindow = _menu.Handle, WindowStyle = 0x50000000,
            UsesPerPixelTransparency = true, TreatAsInputRoot = true
        });
        _source.CompositionTarget.BackgroundColor = Media.Colors.Transparent;
        _source.RootVisual = _surface;
        System.Windows.Input.KeyboardNavigation.SetTabNavigation(_surface, System.Windows.Input.KeyboardNavigationMode.Cycle);
        Forms.Application.AddMessageFilter(this);
        AlignContent(null, EventArgs.Empty);
        _surface.UpdateLayout();
        _frames = 0;
        Media.CompositionTarget.Rendering += FirstFrame;
    }

    private void FirstFrame(object? sender, EventArgs args)
    {
        if (++_frames < 2) return;
        Media.CompositionTarget.Rendering -= FirstFrame;
        _reveal = _surface.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
        {
            _reveal = null;
            if (_source is null || !_menu.Visible) return;
            _target = _menu.Location;
            _distance = _animate ? Math.Max(1, (int)Math.Round(8 * _menu.DeviceDpi / 96d)) : 0;
            if (_menu.Bottom + _distance > Forms.Screen.FromControl(_menu).WorkingArea.Bottom) _distance = -_distance;
            if (_animate) { _menu.Top = _target.Y + _distance; _started = System.Diagnostics.Stopwatch.GetTimestamp(); _timer.Start(); }
            else _menu.Opacity = 1;
            Cloak(false);
        }));
    }

    private void Animate(object? sender, EventArgs args)
    {
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
        var fade = Math.Clamp(elapsed / 240, 0, 1);
        _menu.Opacity = .01 + .99 * fade * fade * (3 - 2 * fade);
        var travel = Math.Clamp(elapsed / 280, 0, 1);
        _menu.Location = new(_target.X, _target.Y + (int)Math.Round(_distance * Math.Pow(1 - travel, 3)));
        if (travel == 1) _timer.Stop();
    }

    private void AlignContent(object? sender, EventArgs args)
    {
        if (_source is not { } source) return;
        // A layered child needs its cached screen coordinates refreshed too.
        SetWindowPos(source.Handle, IntPtr.Zero, 0, 0, _menu.ClientSize.Width, _menu.ClientSize.Height, 0x0014);
    }

    public bool PreFilterMessage(ref Forms.Message message)
    {
        if (_source is not { } source || message.HWnd != source.Handle || message.Msg is < 0x0100 or > 0x0109) return false;
        if (message.Msg == 0x0100 && message.WParam.ToInt32() == 0x1B)
        { _menu.Close(Forms.ToolStripDropDownCloseReason.Keyboard); return true; }
        var input = new Interop.MSG { hwnd = message.HWnd, message = message.Msg, wParam = message.WParam, lParam = message.LParam };
        return Interop.ComponentDispatcher.RaiseThreadMessage(ref input);
    }

    private void Closed(object? sender, Forms.ToolStripDropDownClosedEventArgs args)
    {
        _timer.Stop();
        _reveal?.Abort(); _reveal = null;
        Media.CompositionTarget.Rendering -= FirstFrame;
        Forms.Application.RemoveMessageFilter(this);
        var detached = _source is not null;
        if (_source is { } source) { source.RootVisual = null; source.Dispose(); _source = null; }
        _surface.Child = null;
        if (!_host.Disposing && !_host.IsDisposed) _host.Region = null;
        _emptyHostRegion?.Dispose(); _emptyHostRegion = null;
        if (detached && !_disposed && !_host.Disposing && !_host.IsDisposed) _host.Child = _content;
        if (!_menu.IsDisposed && !_menu.Disposing) _menu.Opacity = 1;
        Cloak(false);
    }

    private void Cloak(bool hidden)
    {
        if (_menu.IsDisposed || (!hidden && !_menu.IsHandleCreated)) return;
        var value = hidden ? 1 : 0;
        _ = DwmSetWindowAttribute(_menu.Handle, 13, ref value, 4);
    }

    private void HostDisposed(object? sender, EventArgs args) => Dispose();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Closed(null, new Forms.ToolStripDropDownClosedEventArgs(Forms.ToolStripDropDownCloseReason.CloseCalled));
        _menu.Opening -= Opening; _menu.Opened -= Opened; _menu.Closed -= Closed;
        _menu.LocationChanged -= AlignContent; _menu.SizeChanged -= AlignContent;
        _host.Disposed -= HostDisposed;
        _timer.Dispose();
    }

    private sealed class CardRenderer : Forms.ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs args) => args.Graphics.Clear(Color.Black);
        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs args) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins(int left, int right, int top, int bottom) { public int Left = left, Right = right, Top = top, Bottom = bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
