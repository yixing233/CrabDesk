using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using Input = System.Windows.Input;
using Media = System.Windows.Media;
using Animation = System.Windows.Media.Animation;
using Automation = System.Windows.Automation.AutomationProperties;

namespace CrabDesk.Runtime;

internal sealed partial class ConnectionDetailsPopup
{
    private WpfControls.Primitives.ToggleButton CreateRadioSwitch(bool animationsEnabled)
    {
        var toggle = new WpfControls.Primitives.ToggleButton
        {
            Width = 48, Height = 32, IsThreeState = false,
            Foreground = SecondaryInk, BorderBrush = SecondaryInk,
            HorizontalAlignment = Wpf.HorizontalAlignment.Right, VerticalAlignment = Wpf.VerticalAlignment.Center,
            Cursor = Input.Cursors.Hand, FocusVisualStyle = null,
            Template = (WpfControls.ControlTemplate)Wpf.Markup.XamlReader.Parse(RadioSwitchTemplate)
        };
        Automation.SetName(toggle, Text + " 开关");
        Automation.SetHelpText(toggle, "开启或关闭" + Text);
        void UpdateVisual(bool animate)
        {
            toggle.ApplyTemplate();
            var offset = (Media.TranslateTransform)toggle.Template.FindName("ThumbOffset", toggle);
            var track = (WpfControls.Border)toggle.Template.FindName("OnTrack", toggle);
            var startX = offset.X;
            var startOpacity = track.Opacity;
            offset.BeginAnimation(Media.TranslateTransform.XProperty, null);
            track.BeginAnimation(Wpf.UIElement.OpacityProperty, null);
            offset.X = toggle.IsChecked is true ? 20 : 0;
            track.Opacity = toggle.IsChecked is true ? 1 : 0;
            if (!animate || !toggle.IsLoaded || !toggle.IsVisible || !animationsEnabled || !Wpf.SystemParameters.ClientAreaAnimation) return;
            var duration = TimeSpan.FromMilliseconds(160);
            offset.BeginAnimation(Media.TranslateTransform.XProperty, new Animation.DoubleAnimation(startX, offset.X, duration)
            {
                EasingFunction = new Animation.CubicEase { EasingMode = Animation.EasingMode.EaseOut },
                FillBehavior = Animation.FillBehavior.Stop
            });
            track.BeginAnimation(Wpf.UIElement.OpacityProperty, new Animation.DoubleAnimation(startOpacity, track.Opacity, duration)
            {
                EasingFunction = new Animation.CubicEase { EasingMode = Animation.EasingMode.EaseOut },
                FillBehavior = Animation.FillBehavior.Stop
            });
        }
        toggle.Checked += (_, _) => UpdateVisual(true);
        toggle.Unchecked += (_, _) => UpdateVisual(true);
        toggle.Loaded += (_, _) => UpdateVisual(false);
        toggle.Unloaded += (_, _) => UpdateVisual(false);
        return toggle;
    }

    private const string RadioSwitchTemplate = """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ToggleButton">
          <Grid x:Name="SwitchRoot" Background="Transparent">
            <Border Width="40" Height="20" CornerRadius="10" BorderThickness="1"
                    BorderBrush="{TemplateBinding BorderBrush}" Background="Transparent"/>
            <Border x:Name="OnTrack" Width="40" Height="20" CornerRadius="10"
                    Background="{DynamicResource MixerAccent}" Opacity="0"/>
            <Border x:Name="Thumb" Width="12" Height="12" CornerRadius="6"
                    HorizontalAlignment="Left" Margin="8,0,0,0" Background="{TemplateBinding Foreground}">
              <Border.RenderTransform><TranslateTransform x:Name="ThumbOffset"/></Border.RenderTransform>
            </Border>
            <Border x:Name="FocusRing" Width="48" Height="28" CornerRadius="14"
                    BorderThickness="1" BorderBrush="{DynamicResource MixerAccent}" Opacity="0" IsHitTestVisible="False"/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsChecked" Value="True"><Setter TargetName="Thumb" Property="Background" Value="White"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="FocusRing" Property="Opacity" Value="0.8"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """;
}
