using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CrabDesk.WinUI.Controls;

/// <summary>
/// A FontIcon control specialized for rendering Lucide icons.
/// </summary>
public class LucideIcon : FontIcon
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon),
        typeof(LucideIconName),
        typeof(LucideIcon),
        new PropertyMetadata(LucideIconName.None, OnIconPropertyChanged));

    private static FontFamily? s_lucideFontFamily;

    public LucideIcon()
    {
        FontFamily = GetLucideFontFamily();
    }

    public LucideIconName Icon
    {
        get => (LucideIconName)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    private static void OnIconPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LucideIcon control && e.NewValue is LucideIconName iconName)
        {
            control.Glyph = LucideGlyphs.ToGlyph(iconName);
        }
    }

    private static FontFamily GetLucideFontFamily()
    {
        if (s_lucideFontFamily != null) return s_lucideFontFamily;
        if (Application.Current?.Resources.TryGetValue("LucideFontFamily", out var resource) == true &&
            resource is FontFamily fontFamily)
        {
            s_lucideFontFamily = fontFamily;
            return fontFamily;
        }
        s_lucideFontFamily = new FontFamily("ms-appx:///Assets/lucide.ttf#lucide");
        return s_lucideFontFamily;
    }
}

/// <summary>
/// An IconSource specialized for rendering Lucide icons.
/// </summary>
public class LucideIconSource : FontIconSource
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon),
        typeof(LucideIconName),
        typeof(LucideIconSource),
        new PropertyMetadata(LucideIconName.None, OnIconPropertyChanged));

    private static FontFamily? s_lucideFontFamily;

    public LucideIconSource()
    {
        FontFamily = GetLucideFontFamily();
    }

    public LucideIconName Icon
    {
        get => (LucideIconName)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    private static void OnIconPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LucideIconSource source && e.NewValue is LucideIconName iconName)
        {
            source.Glyph = LucideGlyphs.ToGlyph(iconName);
        }
    }

    private static FontFamily GetLucideFontFamily()
    {
        if (s_lucideFontFamily != null) return s_lucideFontFamily;
        if (Application.Current?.Resources.TryGetValue("LucideFontFamily", out var resource) == true &&
            resource is FontFamily fontFamily)
        {
            s_lucideFontFamily = fontFamily;
            return fontFamily;
        }
        s_lucideFontFamily = new FontFamily("ms-appx:///Assets/lucide.ttf#lucide");
        return s_lucideFontFamily;
    }
}