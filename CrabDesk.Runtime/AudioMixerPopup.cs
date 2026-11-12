using System.Drawing;
using System.Runtime.InteropServices;
using CrabDesk.Core;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using Input = System.Windows.Input;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;
using Automation = System.Windows.Automation.AutomationProperties;

namespace CrabDesk.Runtime;

internal sealed class AudioMixerPopup : DesktopWidgetInputForm
{
    private readonly AudioMixerController _controller;
    private readonly Forms.Timer _refresh = new() { Interval = 1500 };
    private readonly WpfControls.Border _content;
    private readonly WpfControls.StackPanel _applications = new();
    private readonly WpfControls.TextBlock _status;
    private readonly WpfControls.TextBlock _empty;
    private readonly Media.Brush _foreground;
    private readonly Media.Brush _secondary;
    private readonly WpfControls.ControlTemplate _sliderTemplate;
    private readonly Wpf.Style _buttonStyle;
    private readonly Dictionary<string, MixerRow> _rows = [];
    private readonly MixerRow _master;
    private string _deviceId = "";
    private bool _closing;

    private sealed record MixerRow(WpfControls.Grid Root, WpfControls.TextBlock Name, WpfControls.TextBlock Amount,
        WpfControls.Slider Slider, WpfControls.Button Mute, WpfControls.ContentControl Icon)
    {
        public bool Updating;
        public bool IsMuted;
    }

    internal Wpf.FrameworkElement ContentView => _content;

    internal AudioMixerPopup(AudioMixerController controller, bool dark, Color accent, double scale, Color? background = null,
        bool animationsEnabled = true)
    {
        _controller = controller;
        Text = "应用音量";
        AccessibleName = "应用音量";
        FormBorderStyle = Forms.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = Forms.FormStartPosition.Manual;
        AutoScaleMode = Forms.AutoScaleMode.None;
        ClientSize = new((int)Math.Round(350 * scale), (int)Math.Round(390 * scale));
        BackColor = background ?? (dark ? Color.FromArgb(36, 39, 45) : Color.FromArgb(248, 249, 251));
        _foreground = Brush(dark ? Color.FromArgb(242, 244, 248) : Color.FromArgb(31, 35, 41));
        _secondary = Brush(dark ? Color.FromArgb(176, 183, 195) : Color.FromArgb(102, 110, 124));
        _sliderTemplate = (WpfControls.ControlTemplate)Wpf.Markup.XamlReader.Parse(SliderTemplate);
        _buttonStyle = (Wpf.Style)Wpf.Markup.XamlReader.Parse(ButtonStyle);
        var grid = new WpfControls.Grid();
        grid.RowDefinitions.Add(new() { Height = Wpf.GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = Wpf.GridLength.Auto });
        var header = new WpfControls.Grid { Margin = new Wpf.Thickness(0, 0, 0, 4) };
        header.Children.Add(Label("应用音量", 14, true));
        var close = IconButton(LucideRuntimeIcon.X, "关闭", 24);
        close.HorizontalAlignment = Wpf.HorizontalAlignment.Right;
        close.Click += (_, _) => Dismiss();
        header.Children.Add(close);
        grid.Children.Add(header);

        _master = CreateRow(null, "总音量", LucideRuntimeIcon.Headphones);
        var masterPanel = new WpfControls.StackPanel { Margin = new Wpf.Thickness(0, 0, 0, 4) };
        masterPanel.Children.Add(_master.Root);
        masterPanel.Children.Add(new WpfControls.Border { Height = 1, Margin = new Wpf.Thickness(0, 4, 0, 2),
            Background = Brush(dark ? Color.FromArgb(61, 66, 77) : Color.FromArgb(223, 227, 234)) });
        var body = new WpfControls.Grid();
        body.RowDefinitions.Add(new() { Height = Wpf.GridLength.Auto });
        body.RowDefinitions.Add(new() { Height = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        body.Children.Add(masterPanel);

        _empty = Label("正在读取应用音量…", 12);
        _empty.Foreground = _secondary;
        _empty.TextWrapping = Wpf.TextWrapping.Wrap;
        _empty.Margin = new Wpf.Thickness(2, 8, 2, 8);
        _applications.Children.Add(_empty);
        var scroll = new WpfControls.ScrollViewer
        {
            VerticalScrollBarVisibility = WpfControls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = WpfControls.ScrollBarVisibility.Disabled,
            Content = _applications, Padding = new Wpf.Thickness(0, 0, 4, 0), Focusable = false
        };
        WpfControls.Grid.SetRow(scroll, 1);
        body.Children.Add(scroll);
        var loading = CreatePopupLoading(body, dark, animationsEnabled, PopupSkeletonLayout.Mixer);
        WpfControls.Grid.SetRow(loading, 1);
        grid.Children.Add(loading);
        _status = Label("", 11);
        _status.Foreground = _secondary;
        _status.TextWrapping = Wpf.TextWrapping.Wrap;
        _status.Margin = new Wpf.Thickness(0, 6, 0, 0);
        WpfControls.Grid.SetRow(_status, 2);
        grid.Children.Add(_status);
        _content = new WpfControls.Border
        {
            CornerRadius = new Wpf.CornerRadius(12), Padding = new Wpf.Thickness(12, 8, 12, 10),
            Background = Brush(BackColor), Child = grid
        };
        InitializePopupEntrance(_content, animationsEnabled);
        _content.Resources["MixerAccent"] = Brush(accent);
        _content.Resources["MixerTrack"] = Brush(dark ? Color.FromArgb(88, 95, 109) : Color.FromArgb(191, 198, 211));
        _content.Resources["MixerHover"] = Brush(dark ? Color.FromArgb(61, 67, 80) : Color.FromArgb(228, 233, 242));
        _content.Resources["MixerScrollThumb"] = Brush(dark ? Color.FromArgb(105, 116, 135) : Color.FromArgb(169, 179, 196));
        _content.Resources[typeof(WpfControls.Primitives.ScrollBar)] = Wpf.Markup.XamlReader.Parse(ScrollBarStyle);
        _content.PreviewKeyDown += (_, args) => { if (args.Key == Input.Key.Escape) { args.Handled = true; Dismiss(); } };
        _controller.Changed += OnSnapshotChanged;
        _refresh.Tick += async (_, _) => await _controller.RefreshAsync();
        Shown += async (_, _) =>
        {
            _refresh.Start();
            await _controller.RefreshAsync();
            if (!_closing) _master.Slider.Focus();
        };
        Deactivate += (_, _) => Dismiss();
        ApplySnapshot();
    }

    internal void ShowAt(Forms.IWin32Window owner, Rectangle anchor)
    {
        var area = Forms.Screen.FromRectangle(anchor).WorkingArea;
        Size = new(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
        Location = PopupLocation(anchor, Size, area);
        Show(owner);
        Activate();
    }

    internal static Point PopupLocation(Rectangle anchor, Size size, Rectangle workArea)
    {
        var x = Math.Clamp(anchor.Right - size.Width, workArea.Left, Math.Max(workArea.Left, workArea.Right - size.Width));
        var below = anchor.Bottom + 8;
        var y = below + size.Height <= workArea.Bottom ? below : anchor.Top - size.Height - 8;
        return new(x, Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - size.Height)));
    }

    internal void Dismiss()
    {
        if (_closing || IsDisposed) return;
        _closing = true;
        HideWithoutActivation();
        Close();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        CrabDesk.Native.DesktopPopupWindow.Track(Handle);
        var corners = 2;
        _ = DwmSetWindowAttribute(Handle, 33, ref corners, sizeof(int));
        ApplyNativePopupMaterial();
    }

    private WpfControls.TextBlock Label(string text, double size, bool bold = false) => new()
    {
        Text = text, FontSize = size, Foreground = _foreground,
        FontFamily = new Media.FontFamily(BoxAppearance.DefaultFontFamily),
        FontWeight = bold ? Wpf.FontWeights.SemiBold : Wpf.FontWeights.Normal,
        VerticalAlignment = Wpf.VerticalAlignment.Center, TextTrimming = Wpf.TextTrimming.CharacterEllipsis
    };

    private WpfControls.TextBlock Glyph(LucideRuntimeIcon icon, double size = 16) => new()
    {
        Text = LucideRuntimeIcons.GetGlyph(icon), FontFamily = LucideRuntimeIcons.CreateWpfFontFamily(),
        FontSize = size, Foreground = _foreground,
        HorizontalAlignment = Wpf.HorizontalAlignment.Center, VerticalAlignment = Wpf.VerticalAlignment.Center
    };

    private WpfControls.Button IconButton(LucideRuntimeIcon icon, string name, double size)
    {
        var button = new WpfControls.Button { Content = Glyph(icon), Width = size, Height = size,
            Style = _buttonStyle, ToolTip = name, Cursor = Input.Cursors.Hand };
        Automation.SetName(button, name);
        return button;
    }

    private MixerRow CreateRow(string? id, string name, LucideRuntimeIcon icon)
    {
        var root = new WpfControls.Grid { Height = 44, Margin = new Wpf.Thickness(0, 0, 0, 2) };
        root.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(28) });
        root.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        root.ColumnDefinitions.Add(new() { Width = new Wpf.GridLength(26) });
        var image = new WpfControls.ContentControl { Content = Glyph(icon, 18), Width = 20, Height = 20,
            HorizontalAlignment = Wpf.HorizontalAlignment.Left, VerticalAlignment = Wpf.VerticalAlignment.Bottom,
            Margin = new Wpf.Thickness(0, 0, 0, 2) };
        root.Children.Add(image);
        var content = new WpfControls.Grid();
        content.RowDefinitions.Add(new() { Height = new Wpf.GridLength(20) });
        content.RowDefinitions.Add(new() { Height = new Wpf.GridLength(24) });
        var title = Label(name, 12, id is null);
        title.Margin = new Wpf.Thickness(0, 0, 42, 0);
        title.ToolTip = name;
        content.Children.Add(title);
        var amount = Label("—", 11);
        amount.Foreground = _secondary;
        amount.HorizontalAlignment = Wpf.HorizontalAlignment.Right;
        content.Children.Add(amount);
        var slider = new WpfControls.Slider
        {
            Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 5,
            IsMoveToPointEnabled = true, Template = _sliderTemplate,
            VerticalAlignment = Wpf.VerticalAlignment.Center, Height = 24,
            Cursor = Input.Cursors.Hand
        };
        Automation.SetName(slider, name + "音量");
        WpfControls.Grid.SetRow(slider, 1);
        content.Children.Add(slider);
        WpfControls.Grid.SetColumn(content, 1);
        root.Children.Add(content);
        var mute = IconButton(LucideRuntimeIcon.Volume2, name + "静音", 24);
        mute.VerticalAlignment = Wpf.VerticalAlignment.Bottom;
        mute.Margin = new Wpf.Thickness(2, 0, 0, 0);
        WpfControls.Grid.SetColumn(mute, 2);
        root.Children.Add(mute);
        var row = new MixerRow(root, title, amount, slider, mute, image);
        slider.ValueChanged += (_, args) => { if (!row.Updating) _controller.SetVolume(id, args.NewValue); };
        mute.Click += (_, _) => _controller.SetMuted(id, !row.IsMuted);
        return row;
    }

    private void OnSnapshotChanged()
    {
        if (_closing || IsDisposed) return;
        if (_content.Dispatcher.CheckAccess()) ApplySnapshot();
        else _content.Dispatcher.BeginInvoke((Action)(() => { if (!_closing && !IsDisposed) ApplySnapshot(); }));
    }

    internal void ApplySnapshot()
    {
        var snapshot = _controller.Snapshot;
        _master.Name.Text = string.IsNullOrWhiteSpace(snapshot.DeviceName) ? "总音量" : snapshot.DeviceName;
        _master.Name.ToolTip = _master.Name.Text;
        UpdateRow(_master, snapshot.VolumePercent, snapshot.Muted);
        var ids = snapshot.Applications.Select(app => app.Id).ToArray();
        var changed = _deviceId != snapshot.DeviceId || !_rows.Keys.SequenceEqual(ids);
        if (changed && !_rows.Values.Any(row => row.Slider.IsMouseCaptureWithin))
        {
            _deviceId = snapshot.DeviceId;
            _applications.Children.Clear();
            _rows.Clear();
            foreach (var application in snapshot.Applications)
            {
                var row = CreateRow(application.Id, application.Name,
                    application.Id == "system-sounds" ? LucideRuntimeIcon.Volume2 : LucideRuntimeIcon.AppWindow);
                _rows.Add(application.Id, row);
                _applications.Children.Add(row.Root);
                if (!string.IsNullOrEmpty(application.ExecutablePath)) _ = LoadIconAsync(row, application.ExecutablePath);
            }
        }
        foreach (var application in snapshot.Applications)
            if (_rows.TryGetValue(application.Id, out var row)) UpdateRow(row, application.VolumePercent, application.Muted);
        if (_rows.Count == 0)
        {
            _empty.Text = !_controller.HasSnapshot ? "正在读取应用音量…" : string.IsNullOrEmpty(snapshot.DeviceId)
                ? "未找到可用的音频输出设备" : "暂无音频应用\n打开应用并播放音频后会显示在这里";
            if (!_applications.Children.Contains(_empty)) _applications.Children.Add(_empty);
        }
        _status.Text = _controller.Error;
        _status.Visibility = string.IsNullOrEmpty(_status.Text) ? Wpf.Visibility.Collapsed : Wpf.Visibility.Visible;
        if (_controller.HasSnapshot) Loading!.ShowContent();
    }

    private void UpdateRow(MixerRow row, double? volume, bool? muted)
    {
        row.Updating = true;
        try
        {
            row.Slider.IsEnabled = volume is not null;
            row.Mute.IsEnabled = muted is not null;
            if (!row.Slider.IsMouseCaptureWithin) row.Slider.Value = volume ?? 0;
            row.IsMuted = muted is true;
            row.Amount.Text = volume is { } value ? $"{value:0}%" : "—";
            row.Mute.Content = Glyph(row.IsMuted ? LucideRuntimeIcon.VolumeX : LucideRuntimeIcon.Volume2);
            var action = row.IsMuted ? "取消静音" : "静音";
            row.Mute.ToolTip = action;
            Automation.SetName(row.Mute, row.Name.Text + " · " + action);
            row.Slider.Opacity = row.IsMuted ? 0.55 : 1;
        }
        finally { row.Updating = false; }
    }

    private async Task LoadIconAsync(MixerRow row, string path)
    {
        var image = await Task.Run(() =>
        {
            try
            {
                using var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is null) return null;
                using var bitmap = icon.ToBitmap();
                using var stream = new MemoryStream();
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                stream.Position = 0;
                var frame = Imaging.BitmapFrame.Create(stream, Imaging.BitmapCreateOptions.None, Imaging.BitmapCacheOption.OnLoad);
                frame.Freeze();
                return frame;
            }
            catch (Exception) { return null; }
        });
        if (image is not null && !_closing && !IsDisposed)
            await _content.Dispatcher.InvokeAsync(() =>
            {
                if (!_closing && !IsDisposed)
                    row.Icon.Content = new WpfControls.Image { Source = image, Width = 20, Height = 20, Stretch = Media.Stretch.Uniform };
            });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closing = true;
            _refresh.Dispose();
            _controller.Changed -= OnSnapshotChanged;
            _controller.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Media.SolidColorBrush Brush(Color color)
    {
        var brush = new Media.SolidColorBrush(Media.Color.FromArgb(color.A, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    internal const string ButtonStyle = """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
          <Setter Property="Background" Value="Transparent"/>
          <Setter Property="BorderThickness" Value="0"/>
          <Setter Property="Template"><Setter.Value>
            <ControlTemplate TargetType="Button"><Border Background="{TemplateBinding Background}" CornerRadius="6">
              <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </Border></ControlTemplate>
          </Setter.Value></Setter>
          <Style.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter Property="Background" Value="{DynamicResource MixerHover}"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter Property="Background" Value="{DynamicResource MixerHover}"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
          </Style.Triggers>
        </Style>
        """;

    internal const string SliderTemplate = """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Slider">
          <Grid Height="24" Background="Transparent">
            <Track x:Name="PART_Track" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}"
                   Value="{TemplateBinding Value}" IsDirectionReversed="False">
              <Track.DecreaseRepeatButton>
                <RepeatButton Command="Slider.DecreaseLarge" Focusable="False">
                  <RepeatButton.Template><ControlTemplate TargetType="RepeatButton">
                    <Border Height="3" Background="{DynamicResource MixerAccent}" CornerRadius="1.5"/>
                  </ControlTemplate></RepeatButton.Template>
                </RepeatButton>
              </Track.DecreaseRepeatButton>
              <Track.IncreaseRepeatButton>
                <RepeatButton Command="Slider.IncreaseLarge" Focusable="False">
                  <RepeatButton.Template><ControlTemplate TargetType="RepeatButton">
                    <Border Height="3" Background="{DynamicResource MixerTrack}" CornerRadius="1.5"/>
                  </ControlTemplate></RepeatButton.Template>
                </RepeatButton>
              </Track.IncreaseRepeatButton>
              <Track.Thumb>
                <Thumb Width="16" Height="16">
                  <Thumb.Template><ControlTemplate TargetType="Thumb">
                    <Border Background="White" CornerRadius="8" BorderThickness="1" BorderBrush="#18000000" Padding="3">
                      <Ellipse Fill="{DynamicResource MixerAccent}"/>
                    </Border>
                  </ControlTemplate></Thumb.Template>
                </Thumb>
              </Track.Thumb>
            </Track>
          </Grid>
        </ControlTemplate>
        """;

    internal const string ScrollBarStyle = """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ScrollBar">
          <Setter Property="Width" Value="8"/>
          <Setter Property="Margin" Value="6,0,0,0"/>
          <Setter Property="Background" Value="Transparent"/>
          <Setter Property="Template"><Setter.Value>
            <ControlTemplate TargetType="ScrollBar">
              <Track x:Name="PART_Track" Orientation="Vertical" IsDirectionReversed="True"
                     Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}"
                     Value="{TemplateBinding Value}" ViewportSize="{TemplateBinding ViewportSize}">
                <Track.DecreaseRepeatButton>
                  <RepeatButton Command="{x:Static ScrollBar.PageUpCommand}" Focusable="False">
                    <RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template>
                  </RepeatButton>
                </Track.DecreaseRepeatButton>
                <Track.IncreaseRepeatButton>
                  <RepeatButton Command="{x:Static ScrollBar.PageDownCommand}" Focusable="False">
                    <RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template>
                  </RepeatButton>
                </Track.IncreaseRepeatButton>
                <Track.Thumb><Thumb MinHeight="24">
                  <Thumb.Template><ControlTemplate TargetType="Thumb">
                    <Grid Background="Transparent">
                      <Border x:Name="Indicator" Width="3" HorizontalAlignment="Center"
                              Background="{DynamicResource MixerScrollThumb}" CornerRadius="1.5" Opacity="0.55"/>
                    </Grid>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="Indicator" Property="Width" Value="5"/>
                        <Setter TargetName="Indicator" Property="CornerRadius" Value="2.5"/>
                        <Setter TargetName="Indicator" Property="Opacity" Value="1"/>
                      </Trigger>
                      <Trigger Property="IsDragging" Value="True">
                        <Setter TargetName="Indicator" Property="Width" Value="5"/>
                        <Setter TargetName="Indicator" Property="CornerRadius" Value="2.5"/>
                        <Setter TargetName="Indicator" Property="Opacity" Value="1"/>
                      </Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate></Thumb.Template>
                </Thumb></Track.Thumb>
              </Track>
            </ControlTemplate>
          </Setter.Value></Setter>
        </Style>
        """;
}
