using System.Drawing;
using System.Runtime.InteropServices;
using CrabDesk.Core;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Media = System.Windows.Media;
using Input = System.Windows.Input;
using Automation = System.Windows.Automation.AutomationProperties;

namespace CrabDesk.Runtime;

internal sealed class MicrophonePopup : DesktopWidgetInputForm
{
    private readonly MicrophoneController _controller;
    private readonly Forms.Timer _refresh = new() { Interval = 250 };
    private readonly Wpf.Style _buttonStyle;
    private readonly Media.Brush _foreground, _secondary;
    private readonly Controls.Border _content, _levelTrack, _levelFill;
    private readonly Controls.TextBlock _deviceName, _amount, _levelText, _status;
    private readonly Controls.Button _device, _mute;
    private readonly Controls.Slider _volume;
    private readonly FluentContextMenuStrip _devices;
    private bool _closing, _updating;

    internal Wpf.FrameworkElement ContentView => _content;

    internal MicrophonePopup(MicrophoneController controller, bool dark, Color accent, double scale,
        Action<Forms.ContextMenuStrip>? themeMenu = null, Color? background = null, bool animationsEnabled = true)
    {
        _controller = controller;
        Text = AccessibleName = "麦克风";
        FormBorderStyle = Forms.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = Forms.FormStartPosition.Manual;
        AutoScaleMode = Forms.AutoScaleMode.None;
        ClientSize = new((int)Math.Round(320 * scale), (int)Math.Round(194 * scale));
        BackColor = background ?? (dark ? Color.FromArgb(36, 39, 45) : Color.FromArgb(248, 249, 251));
        _foreground = Brush(dark ? Color.FromArgb(242, 244, 248) : Color.FromArgb(31, 35, 41));
        _secondary = Brush(dark ? Color.FromArgb(176, 183, 195) : Color.FromArgb(102, 110, 124));
        _buttonStyle = (Wpf.Style)Wpf.Markup.XamlReader.Parse(AudioMixerPopup.ButtonStyle);
        _devices = new FluentContextMenuStrip { PreferredDpiScale = (float)scale, OpacityAnimationAllowed = false };
        _devices.Opening += (_, _) =>
        {
            if (themeMenu is not null) themeMenu(_devices);
            else _devices.Renderer = new FluentMenuRenderer(dark);
        };

        var grid = new Controls.Grid();
        grid.RowDefinitions.Add(new() { Height = Wpf.GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = Wpf.GridLength.Auto });
        var stack = new Controls.StackPanel();
        var header = new Controls.Grid { Height = 24, Margin = new Wpf.Thickness(0, 0, 0, 6) };
        header.Children.Add(Label("麦克风", 14, true));
        var close = Button(LucideRuntimeIcon.X, "关闭麦克风控制");
        close.HorizontalAlignment = Wpf.HorizontalAlignment.Right;
        close.Click += (_, _) => Dismiss();
        header.Children.Add(close);
        grid.Children.Add(header);

        _deviceName = Label("正在读取麦克风…", 12);
        _deviceName.Margin = new Wpf.Thickness(8, 0, 28, 0);
        var deviceContent = new Controls.Grid();
        deviceContent.Children.Add(_deviceName);
        var arrow = Glyph(LucideRuntimeIcon.ChevronsUpDown, 13);
        arrow.HorizontalAlignment = Wpf.HorizontalAlignment.Right;
        arrow.Margin = new Wpf.Thickness(0, 0, 8, 0);
        deviceContent.Children.Add(arrow);
        _device = new Controls.Button { Content = deviceContent, Height = 30, Style = _buttonStyle,
            HorizontalContentAlignment = Wpf.HorizontalAlignment.Stretch, Margin = new Wpf.Thickness(0, 0, 0, 10), Cursor = Input.Cursors.Hand };
        // The shared icon template centers its content; stretch the device grid explicitly.
        _device.SizeChanged += (_, _) => deviceContent.Width = Math.Max(0, _device.ActualWidth);
        _device.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "MixerHover");
        Automation.SetName(_device, "选择麦克风输入设备");
        _device.Click += (_, _) => ShowDevices();
        stack.Children.Add(_device);

        var caption = new Controls.Grid { Height = 18 };
        caption.Children.Add(Label("输入音量", 12));
        _amount = Label("—", 11);
        _amount.Foreground = _secondary;
        _amount.HorizontalAlignment = Wpf.HorizontalAlignment.Right;
        caption.Children.Add(_amount);
        stack.Children.Add(caption);
        var volumeRow = new Controls.Grid { Height = 26, Margin = new Wpf.Thickness(0, 0, 0, 6) };
        volumeRow.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        volumeRow.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(30) });
        _volume = new Controls.Slider { Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 5,
            IsMoveToPointEnabled = true, Height = 24, Cursor = Input.Cursors.Hand,
            Template = (Controls.ControlTemplate)Wpf.Markup.XamlReader.Parse(AudioMixerPopup.SliderTemplate) };
        Automation.SetName(_volume, "麦克风输入音量");
        _volume.ValueChanged += (_, args) => { if (!_updating) _controller.SetVolume(args.NewValue); };
        volumeRow.Children.Add(_volume);
        _mute = Button(LucideRuntimeIcon.Mic, "麦克风静音");
        _mute.Click += (_, _) => _controller.SetMuted(_controller.Snapshot.Muted is not true);
        System.Windows.Controls.Grid.SetColumn(_mute, 1);
        volumeRow.Children.Add(_mute);
        stack.Children.Add(volumeRow);

        var levelCaption = new Controls.Grid { Height = 18 };
        var levelLabel = Label("输入电平", 11); levelLabel.Foreground = _secondary;
        levelCaption.Children.Add(levelLabel);
        _levelText = Label("—", 11); _levelText.Foreground = _secondary;
        _levelText.HorizontalAlignment = Wpf.HorizontalAlignment.Right;
        levelCaption.Children.Add(_levelText);
        stack.Children.Add(levelCaption);
        _levelFill = new Controls.Border { CornerRadius = new Wpf.CornerRadius(2), Background = Brush(accent),
            HorizontalAlignment = Wpf.HorizontalAlignment.Left, Width = 0 };
        _levelTrack = new Controls.Border { Height = 4, CornerRadius = new Wpf.CornerRadius(2), Child = _levelFill,
            Margin = new Wpf.Thickness(0, 3, 0, 0) };
        _levelTrack.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "MixerTrack");
        _levelTrack.SizeChanged += (_, _) => UpdateLevel();
        Automation.SetName(_levelTrack, "麦克风输入电平");
        stack.Children.Add(_levelTrack);
        _status = Label("", 11); _status.Foreground = _secondary;
        _status.Margin = new Wpf.Thickness(0, 8, 0, 0);
        System.Windows.Controls.Grid.SetRow(_status, 2);
        grid.Children.Add(_status);
        var loading = CreatePopupLoading(stack, dark, animationsEnabled, PopupSkeletonLayout.Microphone);
        System.Windows.Controls.Grid.SetRow(loading, 1);
        grid.Children.Add(loading);

        _content = new Controls.Border { CornerRadius = new Wpf.CornerRadius(12), Padding = new Wpf.Thickness(12, 10, 12, 10),
            Background = Brush(BackColor), Child = grid };
        InitializePopupEntrance(_content, animationsEnabled);
        _content.Resources["MixerAccent"] = Brush(accent);
        _content.Resources["MixerTrack"] = Brush(dark ? Color.FromArgb(88, 95, 109) : Color.FromArgb(191, 198, 211));
        _content.Resources["MixerHover"] = Brush(dark ? Color.FromArgb(61, 67, 80) : Color.FromArgb(228, 233, 242));
        _content.PreviewKeyDown += (_, args) => { if (args.Key == Input.Key.Escape) { args.Handled = true; Dismiss(); } };
        _controller.Changed += OnChanged;
        _refresh.Tick += async (_, _) => await _controller.RefreshAsync();
        Shown += async (_, _) => { _refresh.Start(); await _controller.RefreshAsync(); if (!_closing) _volume.Focus(); };
        Deactivate += (_, _) => Dismiss();
        ApplySnapshot();
    }

    private void ShowDevices()
    {
        if (_devices.Visible) { _devices.Close(); return; }
        _devices.Items.Clear();
        foreach (var device in _controller.Snapshot.Devices)
        {
            var item = new FluentToolStripMenuItem(device.Name) { Checked = device.Id == _controller.Snapshot.DeviceId };
            item.Click += (_, _) => _controller.SetDevice(device.Id);
            _devices.Items.Add(item);
        }
        if (_devices.Items.Count == 0) return;
        var top = _device.PointToScreen(new Wpf.Point(0, 0));
        var bottom = _device.PointToScreen(new Wpf.Point(_device.ActualWidth, _device.ActualHeight));
        _devices.ToggleSourceBounds = Rectangle.FromLTRB((int)top.X, (int)top.Y, (int)bottom.X, (int)bottom.Y);
        _devices.Show(this, PointToClient(new Point((int)top.X, (int)bottom.Y)));
    }

    private void OnChanged()
    {
        if (_closing || IsDisposed) return;
        if (_content.Dispatcher.CheckAccess()) ApplySnapshot();
        else _content.Dispatcher.BeginInvoke((Action)(() => { if (!_closing && !IsDisposed) ApplySnapshot(); }));
    }

    internal void ApplySnapshot()
    {
        var state = _controller.Snapshot;
        _updating = true;
        try
        {
            _deviceName.Text = !_controller.HasSnapshot ? "正在读取麦克风…" : string.IsNullOrEmpty(state.DeviceName) ? "未找到可用的麦克风" : state.DeviceName;
            _device.ToolTip = _deviceName.Text;
            _device.IsEnabled = state.Devices.Count > 0;
            _volume.IsEnabled = state.VolumePercent is not null;
            if (!_volume.IsMouseCaptureWithin) _volume.Value = state.VolumePercent ?? 0;
            _mute.IsEnabled = state.Muted is not null;
            _mute.Content = Glyph(state.Muted is true ? LucideRuntimeIcon.MicOff : LucideRuntimeIcon.Mic);
            var action = state.Muted is true ? "取消麦克风静音" : "麦克风静音";
            _mute.ToolTip = action; Automation.SetName(_mute, action);
            _amount.Text = state.VolumePercent is { } level ? $"{level:0}%" + (state.Muted is true ? " · 已静音" : "") : "—";
            UpdateLevel();
            _status.Text = _controller.Error;
            _status.Visibility = string.IsNullOrEmpty(_status.Text) ? Wpf.Visibility.Collapsed : Wpf.Visibility.Visible;
        }
        finally { _updating = false; }
        if (_controller.HasSnapshot) Loading!.ShowContent();
    }

    private void UpdateLevel()
    {
        var state = _controller.Snapshot;
        var peak = state.Muted is true ? 0 : ControlCenterPresentation.ClampPercent(state.PeakPercent ?? 0);
        _levelFill.Width = _levelTrack.ActualWidth * peak / 100;
        _levelText.Text = state.Muted is true ? "已静音" : state.PeakPercent is null ? "—" : $"{peak:0}%";
    }

    internal void ShowAt(Forms.IWin32Window owner, Rectangle anchor)
    {
        var area = Forms.Screen.FromRectangle(anchor).WorkingArea;
        Location = AudioMixerPopup.PopupLocation(anchor, Size, area);
        Show(owner);
        Activate();
    }

    internal void Dismiss()
    {
        if (_closing || IsDisposed) return;
        _closing = true;
        _refresh.Stop();
        _devices.Close();
        HideWithoutActivation();
        Close();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var corners = 2;
        _ = DwmSetWindowAttribute(Handle, 33, ref corners, sizeof(int));
        ApplyNativePopupMaterial();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closing = true;
            _refresh.Dispose();
            _devices.Dispose();
            _controller.Changed -= OnChanged;
            _controller.Dispose();
        }
        base.Dispose(disposing);
    }

    private Controls.TextBlock Label(string text, double size, bool bold = false) => new()
    {
        Text = text, FontSize = size, Foreground = _foreground, FontFamily = new Media.FontFamily(BoxAppearance.DefaultFontFamily),
        FontWeight = bold ? Wpf.FontWeights.SemiBold : Wpf.FontWeights.Normal,
        VerticalAlignment = Wpf.VerticalAlignment.Center, TextTrimming = Wpf.TextTrimming.CharacterEllipsis
    };

    private Controls.TextBlock Glyph(LucideRuntimeIcon icon, double size = 16) => new()
    {
        Text = LucideRuntimeIcons.GetGlyph(icon), FontFamily = LucideRuntimeIcons.CreateWpfFontFamily(), FontSize = size,
        Foreground = _foreground, VerticalAlignment = Wpf.VerticalAlignment.Center, HorizontalAlignment = Wpf.HorizontalAlignment.Center
    };

    private Controls.Button Button(LucideRuntimeIcon icon, string name)
    {
        var button = new Controls.Button { Content = Glyph(icon), Width = 24, Height = 24,
            Style = _buttonStyle, ToolTip = name, Cursor = Input.Cursors.Hand };
        Automation.SetName(button, name);
        return button;
    }

    private static Media.SolidColorBrush Brush(Color color)
    {
        var brush = new Media.SolidColorBrush(Media.Color.FromArgb(color.A, color.R, color.G, color.B));
        brush.Freeze(); return brush;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
