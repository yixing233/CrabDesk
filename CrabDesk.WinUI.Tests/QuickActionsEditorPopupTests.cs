using System.Drawing;
using System.Runtime.ExceptionServices;
using CrabDesk.Core;
using CrabDesk.Runtime;
using Xunit;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;
using Automation = System.Windows.Automation.AutomationProperties;
using QuickAction = CrabDesk.Core.ControlCenterQuickAction;

namespace CrabDesk.WinUI.Tests;

public sealed class QuickActionsEditorPopupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PopupRendersAllActionsInSelectedOrderWithAccessibleControls(bool dark) => OnSta(() =>
    {
        QuickAction[] selected = [QuickAction.Settings, QuickAction.Bluetooth, QuickAction.Display,
            QuickAction.Cast, QuickAction.Microphone, QuickAction.WiFi];
        var updates = new List<IReadOnlyList<QuickAction>>();
        using var popup = new QuickActionsEditorPopup(selected, updates.Add, dark, Color.FromArgb(79, 100, 154), 1);
        Layout(popup);

        var checkboxes = Descendants<WpfControls.CheckBox>(popup.ContentView).ToArray();
        Assert.Equal(10, checkboxes.Length);
        Assert.Equal(new[] { "设置", "蓝牙", "显示", "投屏", "麦克风", "Wi-Fi", "系统主题", "应用音量", "截图", "网卡" },
            checkboxes.Select(Automation.GetName));
        Assert.Equal(6, checkboxes.Count(check => check.IsChecked == true));
        Assert.All(checkboxes, check => Assert.True(check.ActualWidth > 180));
        Assert.True(Assert.Single(Descendants<WpfControls.ScrollViewer>(popup.ContentView)).ViewportHeight > 200);
        Assert.False(Button(popup, "上移 设置").IsEnabled);
        Assert.False(Button(popup, "下移 Wi-Fi").IsEnabled);
        Assert.Equal(Wpf.Visibility.Hidden, Button(popup, "上移 截图").Visibility);
        Assert.Empty(updates);

        var size = popup.ClientSize;
        var bitmap = new Imaging.RenderTargetBitmap(size.Width, size.Height, 96, 96, Media.PixelFormats.Pbgra32);
        bitmap.Render(popup.ContentView);
        var encoder = new Imaging.PngBitmapEncoder();
        encoder.Frames.Add(Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(AppContext.BaseDirectory,
            dark ? "quick-actions-editor-dark.png" : "quick-actions-editor-light.png"));
        encoder.Save(file);
    });

    [Fact]
    public void SelectingAndHidingAnActionImmediatelyPublishesTheVisibleOrder() => OnSta(() =>
    {
        var updates = new List<IReadOnlyList<QuickAction>>();
        using var popup = new QuickActionsEditorPopup(ControlQuickActions.Defaults, updates.Add, false, Color.RoyalBlue, 1);
        Assert.Empty(updates);

        CheckBox(popup, "投屏").IsChecked = true;

        Assert.Equal(new[] { QuickAction.WiFi, QuickAction.Bluetooth, QuickAction.NightLight, QuickAction.Display, QuickAction.Cast },
            Assert.Single(updates));
        Assert.Equal(new[] { "Wi-Fi", "蓝牙", "系统主题", "显示", "投屏" }, SelectedNames(popup));

        CheckBox(popup, "蓝牙").IsChecked = false;

        Assert.Equal(2, updates.Count);
        Assert.Equal(new[] { QuickAction.WiFi, QuickAction.NightLight, QuickAction.Display, QuickAction.Cast }, updates[1]);
        Assert.Equal(new[] { "Wi-Fi", "系统主题", "显示", "投屏", "蓝牙", "麦克风", "应用音量", "截图", "设置", "网卡" },
            Descendants<WpfControls.CheckBox>(popup.ContentView).Select(Automation.GetName));
        Assert.Equal(Wpf.Visibility.Hidden, Button(popup, "下移 蓝牙").Visibility);
    });

    [Fact]
    public void MovingASelectedActionChangesItsPositionAndPublishesTheNewOrder() => OnSta(() =>
    {
        var updates = new List<IReadOnlyList<QuickAction>>();
        using var popup = new QuickActionsEditorPopup([QuickAction.WiFi, QuickAction.Bluetooth, QuickAction.Display],
            updates.Add, false, Color.RoyalBlue, 1);

        Click(Button(popup, "上移 蓝牙"));

        Assert.Equal(new[] { QuickAction.Bluetooth, QuickAction.WiFi, QuickAction.Display }, Assert.Single(updates));
        Assert.Equal(new[] { "蓝牙", "Wi-Fi", "显示" }, SelectedNames(popup));
        Assert.False(Button(popup, "上移 蓝牙").IsEnabled);
        Assert.True(Button(popup, "下移 蓝牙").IsEnabled);

        Click(Button(popup, "下移 蓝牙"));

        Assert.Equal(2, updates.Count);
        Assert.Equal(new[] { QuickAction.WiFi, QuickAction.Bluetooth, QuickAction.Display }, updates[1]);
        Assert.Equal(new[] { "Wi-Fi", "蓝牙", "显示" }, SelectedNames(popup));
    });

    [Fact]
    public void MoveBoundariesAndUnselectedActionsCannotChangeTheSelection() => OnSta(() =>
    {
        var updates = new List<IReadOnlyList<QuickAction>>();
        using var popup = new QuickActionsEditorPopup([QuickAction.WiFi, QuickAction.Bluetooth],
            updates.Add, false, Color.RoyalBlue, 1);

        Assert.False(Button(popup, "上移 Wi-Fi").IsEnabled);
        Assert.False(Button(popup, "下移 蓝牙").IsEnabled);
        Click(Button(popup, "上移 Wi-Fi"));
        Click(Button(popup, "下移 蓝牙"));
        Click(Button(popup, "上移 投屏"));

        Assert.Empty(updates);
        Assert.Equal(new[] { "Wi-Fi", "蓝牙" }, SelectedNames(popup));
    });

    [Fact]
    public void TheLastSelectedActionCannotBeHidden() => OnSta(() =>
    {
        var updates = new List<IReadOnlyList<QuickAction>>();
        using var popup = new QuickActionsEditorPopup([QuickAction.WiFi], updates.Add, false, Color.RoyalBlue, 1);

        CheckBox(popup, "Wi-Fi").IsChecked = false;

        Assert.True(CheckBox(popup, "Wi-Fi").IsChecked);
        Assert.Empty(updates);
        Assert.Equal(new[] { "Wi-Fi" }, SelectedNames(popup));
        Assert.Contains(Descendants<WpfControls.TextBlock>(popup.ContentView), text => text.Text.Contains("至少保留 1 项"));
    });

    [Fact]
    public void ASeventhActionIsRejectedUntilAnotherSelectionIsRemoved() => OnSta(() =>
    {
        QuickAction[] selected = [QuickAction.WiFi, QuickAction.Bluetooth, QuickAction.NightLight,
            QuickAction.Display, QuickAction.Cast, QuickAction.Microphone];
        var updates = new List<IReadOnlyList<QuickAction>>();
        using var popup = new QuickActionsEditorPopup(selected, updates.Add, false, Color.RoyalBlue, 1);

        CheckBox(popup, "设置").IsChecked = true;

        Assert.False(CheckBox(popup, "设置").IsChecked);
        Assert.Empty(updates);
        Assert.Equal(6, SelectedNames(popup).Length);
        Assert.Contains(Descendants<WpfControls.TextBlock>(popup.ContentView), text => text.Text.Contains("最多选择 6 项"));

        CheckBox(popup, "投屏").IsChecked = false;
        CheckBox(popup, "设置").IsChecked = true;

        Assert.Equal(2, updates.Count);
        Assert.Equal(new[] { QuickAction.WiFi, QuickAction.Bluetooth, QuickAction.NightLight,
            QuickAction.Display, QuickAction.Microphone, QuickAction.Settings }, updates[1]);
        Assert.DoesNotContain(Descendants<WpfControls.TextBlock>(popup.ContentView), text => text.Text.Contains("最多选择 6 项"));
    });

    [Fact]
    public void RestoreDefaultsResetsSelectionAndOrderWithOneNotification() => OnSta(() =>
    {
        var updates = new List<IReadOnlyList<QuickAction>>();
        using var popup = new QuickActionsEditorPopup([QuickAction.Settings, QuickAction.Cast],
            updates.Add, false, Color.RoyalBlue, 1);

        Click(Button(popup, "恢复默认"));

        Assert.Equal(ControlQuickActions.Defaults, Assert.Single(updates));
        Assert.Equal(new[] { "Wi-Fi", "蓝牙", "系统主题", "显示" }, SelectedNames(popup));
        Assert.False(Button(popup, "恢复默认").IsEnabled);
        Click(Button(popup, "恢复默认"));
        Assert.Single(updates);
    });

    [Fact]
    public void InputAndCallbackListsRemainIndependentOfSubsequentEdits() => OnSta(() =>
    {
        var input = ControlQuickActions.Defaults.ToList();
        var updates = new List<IReadOnlyList<QuickAction>>();
        using var popup = new QuickActionsEditorPopup(input, updates.Add, false, Color.RoyalBlue, 1);
        input.Clear();
        Assert.Equal(new[] { "Wi-Fi", "蓝牙", "系统主题", "显示" }, SelectedNames(popup));

        CheckBox(popup, "投屏").IsChecked = true;
        var first = Assert.Single(updates);
        CheckBox(popup, "蓝牙").IsChecked = false;

        Assert.NotSame(first, updates[1]);
        Assert.Equal(new[] { QuickAction.WiFi, QuickAction.Bluetooth, QuickAction.NightLight, QuickAction.Display, QuickAction.Cast }, first);
        Assert.DoesNotContain(QuickAction.Bluetooth, updates[1]);
        Assert.Empty(input);
    });

    private static string[] SelectedNames(QuickActionsEditorPopup popup)
    {
        Layout(popup);
        return Descendants<WpfControls.CheckBox>(popup.ContentView).Where(check => check.IsChecked == true)
            .Select(Automation.GetName).ToArray();
    }

    private static WpfControls.CheckBox CheckBox(QuickActionsEditorPopup popup, string name)
    {
        Layout(popup);
        return Assert.Single(Descendants<WpfControls.CheckBox>(popup.ContentView), check => Automation.GetName(check) == name);
    }

    private static WpfControls.Button Button(QuickActionsEditorPopup popup, string name)
    {
        Layout(popup);
        return Assert.Single(Descendants<WpfControls.Button>(popup.ContentView), button => Automation.GetName(button) == name);
    }

    private static void Click(WpfControls.Button button) => button.RaiseEvent(new Wpf.RoutedEventArgs(WpfControls.Button.ClickEvent));

    private static void Layout(QuickActionsEditorPopup popup)
    {
        var size = popup.ClientSize;
        popup.ContentView.Measure(new Wpf.Size(size.Width, size.Height));
        popup.ContentView.Arrange(new Wpf.Rect(0, 0, size.Width, size.Height));
        popup.ContentView.UpdateLayout();
    }

    private static IEnumerable<T> Descendants<T>(Wpf.DependencyObject parent) where T : Wpf.DependencyObject
    {
        if (parent is T match) yield return match;
        for (var index = 0; index < Media.VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in Descendants<T>(Media.VisualTreeHelper.GetChild(parent, index))) yield return child;
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { failure = exception; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
