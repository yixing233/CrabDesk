using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CrabDesk.App;

internal sealed class TextInputDialog : Window
{
    private readonly TextBox _input;

    private TextInputDialog(string title, string label, string initialValue)
    {
        Title = title;
        Width = 360;
        Height = 165;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;

        var grid = new Grid { Margin = new Thickness(18) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text = label, Margin = new Thickness(4, 0, 4, 4) });
        _input = new TextBox { Text = initialValue };
        _input.SelectAll();
        Grid.SetRow(_input, 1);
        grid.Children.Add(_input);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var ok = new Button { Content = "确定", IsDefault = true };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_input.Text))
            {
                return;
            }
            DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);
        Content = grid;
        Loaded += (_, _) => Keyboard.Focus(_input);
    }

    internal static string? Show(string title, string label, string initialValue)
    {
        var dialog = new TextInputDialog(title, label, initialValue);
        return dialog.ShowDialog() == true ? dialog._input.Text.Trim() : null;
    }
}
