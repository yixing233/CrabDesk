using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CrabDesk.WinUI.Controls;

/// <summary>
/// A FontIcon control specialized for rendering Tabler icons.
/// </summary>
public class TablerIcon : FontIcon
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon),
        typeof(TablerIconName),
        typeof(TablerIcon),
        new PropertyMetadata(TablerIconName.None, OnIconPropertyChanged));

    private static FontFamily? s_tablerFontFamily;

    public TablerIcon()
    {
        FontFamily = GetTablerFontFamily();
    }

    public TablerIconName Icon
    {
        get => (TablerIconName)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    private static void OnIconPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TablerIcon control && e.NewValue is TablerIconName iconName)
        {
            control.Glyph = TablerGlyphs.ToGlyph(iconName);
        }
    }

    private static FontFamily GetTablerFontFamily()
    {
        if (s_tablerFontFamily != null) return s_tablerFontFamily;
        if (Application.Current?.Resources.TryGetValue("TablerFontFamily", out var resource) == true &&
            resource is FontFamily fontFamily)
        {
            s_tablerFontFamily = fontFamily;
            return fontFamily;
        }
        s_tablerFontFamily = new FontFamily("ms-appx:///Assets/tabler-icons.ttf#tabler-icons");
        return s_tablerFontFamily;
    }
}

/// <summary>
/// An IconSource specialized for rendering Tabler icons.
/// </summary>
public class TablerIconSource : FontIconSource
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon),
        typeof(TablerIconName),
        typeof(TablerIconSource),
        new PropertyMetadata(TablerIconName.None, OnIconPropertyChanged));

    private static FontFamily? s_tablerFontFamily;

    public TablerIconSource()
    {
        FontFamily = GetTablerFontFamily();
    }

    public TablerIconName Icon
    {
        get => (TablerIconName)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    private static void OnIconPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TablerIconSource source && e.NewValue is TablerIconName iconName)
        {
            source.Glyph = TablerGlyphs.ToGlyph(iconName);
        }
    }

    private static FontFamily GetTablerFontFamily()
    {
        if (s_tablerFontFamily != null) return s_tablerFontFamily;
        if (Application.Current?.Resources.TryGetValue("TablerFontFamily", out var resource) == true &&
            resource is FontFamily fontFamily)
        {
            s_tablerFontFamily = fontFamily;
            return fontFamily;
        }
        s_tablerFontFamily = new FontFamily("ms-appx:///Assets/tabler-icons.ttf#tabler-icons");
        return s_tablerFontFamily;
    }
}
