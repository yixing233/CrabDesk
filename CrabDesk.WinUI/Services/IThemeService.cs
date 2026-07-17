using CrabDesk.Core;
using Microsoft.UI.Xaml;

namespace CrabDesk.WinUI.Services;

public interface IThemeService
{
    ApplicationThemeMode CurrentMode { get; }
    void RegisterRoot(FrameworkElement root);
    void Apply(ApplicationThemeMode mode);
}

public sealed class ThemeService : IThemeService
{
    private FrameworkElement? _root;
    public ApplicationThemeMode CurrentMode { get; private set; } = ApplicationThemeMode.System;

    public void RegisterRoot(FrameworkElement root)
    {
        _root = root;
        Apply(CurrentMode);
    }

    public void Apply(ApplicationThemeMode mode)
    {
        CurrentMode = mode;
        if (_root is null)
        {
            return;
        }
        _root.RequestedTheme = mode switch
        {
            ApplicationThemeMode.Light => ElementTheme.Light,
            ApplicationThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }
}
