using System.Drawing;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

/// <summary>
/// The box outline is derived from the box background by default, but its
/// width, color and opacity are all user-configurable. These pin the derived
/// behavior and the clamping of each configured value.
/// </summary>
public sealed class BoxBorderStyleTests
{
    private static Color Resolve(
        Color background,
        bool isDarkSurface = true,
        string? color = "Auto",
        double opacity = 100) =>
        BoxBorderStyle.ResolveStroke(background, isDarkSurface, color, opacity);

    [Fact]
    public void StrokeOnADarkBoxIsLighterThanItsBackground()
    {
        var background = Color.FromArgb(42, 45, 50);

        var stroke = Resolve(background);

        Assert.True(Luminance(stroke) > Luminance(background),
            "A dark box needs a lighter rim or the outline disappears into the fill.");
    }

    [Fact]
    public void StrokeOnALightBoxIsDarkerThanItsBackground()
    {
        var background = Color.FromArgb(238, 240, 244);

        var stroke = Resolve(background, isDarkSurface: false);

        Assert.True(Luminance(stroke) < Luminance(background),
            "A light box needs a darker rim or the outline disappears into the fill.");
    }

    [Fact]
    public void AutoStrokeKeepsTheBackgroundHueInsteadOfReplacingIt()
    {
        // A blue box must keep a blue-tinted rim: shifting toward white or
        // black preserves the channel order, so blue stays the strongest
        // channel in both directions.
        var blue = Color.FromArgb(40, 80, 200);

        var dark = Resolve(blue, isDarkSurface: true);
        var light = Resolve(blue, isDarkSurface: false);

        Assert.True(dark.B > dark.R && dark.B > dark.G);
        Assert.True(light.B > light.R && light.B > light.G);
    }

    [Fact]
    public void ConfiguredColorReplacesTheDerivedStroke()
    {
        var stroke = Resolve(
            Color.FromArgb(42, 45, 50),
            color: "#FF3B82F6",
            opacity: 100);

        Assert.Equal(Color.FromArgb(255, 0x3B, 0x82, 0xF6), stroke);
    }

    [Fact]
    public void ConfiguredColorAcceptsSixAndEightDigitHex()
    {
        Assert.True(BoxBorderStyle.TryParseColor("#3B82F6", out var six));
        Assert.Equal(Color.FromArgb(255, 0x3B, 0x82, 0xF6), six);
        Assert.True(BoxBorderStyle.TryParseColor("803B82F6", out var eight));
        Assert.Equal(Color.FromArgb(255, 0x3B, 0x82, 0xF6), eight);
    }

    [Fact]
    public void UnparseableColorFallsBackToTheDerivedStroke()
    {
        // A corrupt value must not blank the outline; it degrades to auto.
        var derived = Resolve(Color.FromArgb(42, 45, 50));
        var corrupt = Resolve(Color.FromArgb(42, 45, 50), color: "not-a-color");

        Assert.Equal(derived, corrupt);
    }

    [Fact]
    public void OpacitySettingDrivesTheStrokeAlpha()
    {
        var background = Color.FromArgb(42, 45, 50);

        Assert.Equal(255, Resolve(background, opacity: 100).A);
        Assert.Equal(128, Resolve(background, opacity: 50).A);
        Assert.Equal(0, Resolve(background, opacity: 0).A);
    }

    [Fact]
    public void OpacityIsClampedRatherThanTrusted()
    {
        var background = Color.FromArgb(42, 45, 50);

        Assert.Equal(255, Resolve(background, opacity: 400).A);
        Assert.Equal(0, Resolve(background, opacity: -50).A);
    }

    [Fact]
    public void WidthIsClampedToTheSupportedRange()
    {
        Assert.Equal(1f, BoxBorderStyle.ResolveWidth(0));
        Assert.Equal(1f, BoxBorderStyle.ResolveWidth(-3));
        Assert.Equal(6f, BoxBorderStyle.ResolveWidth(99));
        Assert.Equal(3f, BoxBorderStyle.ResolveWidth(3));
    }

    [Fact]
    public void AutoIsRecognizedRegardlessOfCaseAndWhitespace()
    {
        Assert.True(BoxBorderStyle.IsAuto("Auto"));
        Assert.True(BoxBorderStyle.IsAuto("auto"));
        Assert.True(BoxBorderStyle.IsAuto("  AUTO  "));
        Assert.True(BoxBorderStyle.IsAuto(null));
        Assert.True(BoxBorderStyle.IsAuto(string.Empty));
        Assert.False(BoxBorderStyle.IsAuto("#FF0000"));
    }

    [Fact]
    public void StrokeAlphaFollowsTheBoxTintOpacity()
    {
        var stroke = Resolve(Color.FromArgb(42, 45, 50));

        // A box at full tint keeps the configured alpha; a faintly tinted or
        // acrylic box gets a proportionally fainter rim instead of a solid one.
        Assert.Equal(stroke.A, BoxBorderStyle.ApplyTint(stroke, 1).A);
        Assert.Equal(
            (int)Math.Round(stroke.A * 0.5),
            BoxBorderStyle.ApplyTint(stroke, 0.5).A);
        Assert.Equal(0, BoxBorderStyle.ApplyTint(stroke, 0).A);
    }

    [Fact]
    public void TintOpacityIsClampedRatherThanTrusted()
    {
        var stroke = Resolve(Color.FromArgb(42, 45, 50));

        Assert.Equal(stroke.A, BoxBorderStyle.ApplyTint(stroke, 4).A);
        Assert.Equal(0, BoxBorderStyle.ApplyTint(stroke, -1).A);
    }

    private static double Luminance(Color color) =>
        0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B;
}
