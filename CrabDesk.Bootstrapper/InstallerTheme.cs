using System.Drawing;
using Microsoft.Win32;

namespace CrabDesk.Bootstrapper;

internal sealed class InstallerTheme
{
    public float DpiScale { get; }
    public bool IsDark { get; }

    public Color Background { get; }
    public Color Surface { get; }
    public Color CardBackground { get; }
    public Color BorderColor { get; }
    public Color TextPrimary { get; }
    public Color TextSecondary { get; }
    public Color TextMuted { get; }
    public Color Accent { get; }
    public Color AccentHover { get; }
    public Color AccentPressed { get; }
    public Color ButtonSecondaryBackground { get; }
    public Color ButtonSecondaryHover { get; }
    public Color ProgressBarTrack { get; }
    public Color SuccessColor { get; }
    public Color ErrorColor { get; }
    public Color WarningColor { get; }

    public Font TitleFont { get; }
    public Font HeaderFont { get; }
    public Font NormalFont { get; }
    public Font SmallFont { get; }
    public Font ButtonFont { get; }
    public Font BadgeFont { get; }

    public InstallerTheme(float dpiScale = 1.0f, bool? forceDark = null)
    {
        DpiScale = Math.Max(1.0f, dpiScale);
        IsDark = forceDark ?? DetectSystemDarkMode();

        if (IsDark)
        {
            Background = Color.FromArgb(32, 32, 32);
            Surface = Color.FromArgb(43, 43, 43);
            CardBackground = Color.FromArgb(38, 38, 38);
            BorderColor = Color.FromArgb(60, 60, 60);
            TextPrimary = Color.FromArgb(245, 245, 245);
            TextSecondary = Color.FromArgb(170, 170, 170);
            TextMuted = Color.FromArgb(120, 120, 120);
            Accent = Color.FromArgb(240, 96, 40); // Crab Accent Orange
            AccentHover = Color.FromArgb(250, 115, 60);
            AccentPressed = Color.FromArgb(220, 80, 25);
            ButtonSecondaryBackground = Color.FromArgb(50, 50, 50);
            ButtonSecondaryHover = Color.FromArgb(65, 65, 65);
            ProgressBarTrack = Color.FromArgb(55, 55, 55);
            SuccessColor = Color.FromArgb(108, 203, 95);
            ErrorColor = Color.FromArgb(255, 107, 107);
            WarningColor = Color.FromArgb(255, 185, 0);
        }
        else
        {
            Background = Color.FromArgb(243, 243, 243);
            Surface = Color.FromArgb(255, 255, 255);
            CardBackground = Color.FromArgb(250, 250, 250);
            BorderColor = Color.FromArgb(225, 225, 225);
            TextPrimary = Color.FromArgb(24, 24, 24);
            TextSecondary = Color.FromArgb(95, 95, 95);
            TextMuted = Color.FromArgb(140, 140, 140);
            Accent = Color.FromArgb(225, 78, 20); // Crab Accent Orange
            AccentHover = Color.FromArgb(240, 95, 40);
            AccentPressed = Color.FromArgb(200, 65, 10);
            ButtonSecondaryBackground = Color.FromArgb(235, 235, 235);
            ButtonSecondaryHover = Color.FromArgb(225, 225, 225);
            ProgressBarTrack = Color.FromArgb(220, 220, 220);
            SuccessColor = Color.FromArgb(16, 124, 16);
            ErrorColor = Color.FromArgb(209, 52, 56);
            WarningColor = Color.FromArgb(190, 130, 0);
        }

        var baseFontName = GetPreferredFontName();
        TitleFont = new Font(baseFontName, 18f * DpiScale, FontStyle.Bold, GraphicsUnit.Pixel);
        HeaderFont = new Font(baseFontName, 14f * DpiScale, FontStyle.Bold, GraphicsUnit.Pixel);
        NormalFont = new Font(baseFontName, 12.5f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel);
        SmallFont = new Font(baseFontName, 11.5f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel);
        ButtonFont = new Font(baseFontName, 12.5f * DpiScale, FontStyle.Bold, GraphicsUnit.Pixel);
        BadgeFont = new Font(baseFontName, 11f * DpiScale, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private static string GetPreferredFontName()
    {
        var fonts = new[] { "Microsoft YaHei UI", "Segoe UI", "Tahoma", "Arial" };
        foreach (var font in fonts)
        {
            using var test = new Font(font, 12f, GraphicsUnit.Pixel);
            if (test.Name.Equals(font, StringComparison.OrdinalIgnoreCase))
            {
                return font;
            }
        }
        return "Microsoft YaHei UI";
    }

    private static bool DetectSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int lightTheme)
            {
                return lightTheme == 0;
            }
        }
        catch
        {
        }
        return false;
    }
}
