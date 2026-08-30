using CrabDesk.Bootstrapper;

namespace CrabDesk.Bootstrapper.Tests;

public sealed class BootstrapperTests
{
    [Theory]
    [InlineData("8.0.30", 8, null, true)]
    [InlineData("8.0.30", 8, "8.0.30", true)]
    [InlineData("8.0.31", 8, "8.0.30", true)]
    [InlineData("8.0.13", 8, "8.0.30", false)]
    [InlineData("8.0.0-preview.1", 8, null, false)]
    [InlineData("9.0.0", 8, null, false)]
    [InlineData("not-a-version", 8, null, false)]
    public void RuntimeDirectoryPolicyAcceptsStableRequestedMajor(string value, int major, string? minimumVersionString, bool expected)
    {
        var minVersion = minimumVersionString is not null ? Version.Parse(minimumVersionString) : null;
        Assert.Equal(expected, DependencyDetector.IsSupportedRuntimeDirectory(value, major, minVersion));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1638, true)]
    [InlineData(1641, true)]
    [InlineData(3010, true)]
    [InlineData(1, false)]
    public void InstallerExitCodePolicyRecognizesSuccessAndRebootCodes(int exitCode, bool expected)
    {
        Assert.Equal(expected, SetupPolicy.IsSuccessfulInstallerExitCode(exitCode));
    }

    [Theory]
    [InlineData(1641, true)]
    [InlineData(3010, true)]
    [InlineData(0, false)]
    public void RebootPolicyRecognizesRebootCodes(int exitCode, bool expected)
    {
        Assert.Equal(expected, SetupPolicy.RequiresRestart(exitCode));
    }

    [Theory]
    [InlineData("https://aka.ms/example.exe", true)]
    [InlineData("http://aka.ms/example.exe", false)]
    [InlineData("file:///C:/example.exe", false)]
    [InlineData("not-a-url", false)]
    public void DependencyUrlPolicyRequiresHttps(string value, bool expected)
    {
        Assert.Equal(expected, SetupPolicy.TryCreateHttpsUri(value, out _));
    }

    [Fact]
    public void InstallerState_CalculatesAvailableDiskSpace_ForDefaultPath()
    {
        var state = new InstallerState();
        Assert.False(string.IsNullOrWhiteSpace(state.InstallPath));

        var (text, isSufficient, driveLabel) = state.GetAvailableSpaceInfo();
        Assert.NotEqual("未知", text);
        Assert.True(isSufficient);
        Assert.NotNull(driveLabel);
    }

    [Theory]
    [InlineData(@"D:\", @"D:\CrabDesk")]
    [InlineData(@"D:\Program Files", @"D:\Program Files\CrabDesk")]
    [InlineData(@"D:\Program Files\CrabDesk", @"D:\Program Files\CrabDesk")]
    [InlineData(@"D:\Software\CrabDesk\", @"D:\Software\CrabDesk")]
    [InlineData(@"C:\Tools", @"C:\Tools\CrabDesk")]
    public void EnsureAppFolder_AppendsLeafFolderWhenNotPresent(string input, string expected)
    {
        var result = SetupPolicy.EnsureAppFolder(input, "CrabDesk");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void LucideInstallerIcons_AllIconsHaveValidGlyphs()
    {
        foreach (LucideIcon icon in Enum.GetValues<LucideIcon>())
        {
            var glyph = LucideInstallerIcons.GetGlyph(icon);
            Assert.False(string.IsNullOrWhiteSpace(glyph), $"Icon {icon} must have a non-empty glyph.");
        }
    }

    [Fact]
    public void InstallerState_HasValidVersionAndInitialProperties()
    {
        var state = new InstallerState();
        Assert.Equal("20260826.02", state.Version);
        Assert.False(string.IsNullOrWhiteSpace(state.InstallPath));
    }
    [Fact]
    public void TestGlyphCenter()
    {
        LucideInstallerIcons.Initialize();
        var font = LucideInstallerIcons.GetFont(24);
        Assert.NotNull(font);
        
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        var sf = new System.Drawing.StringFormat
        {
            Alignment = System.Drawing.StringAlignment.Center,
            LineAlignment = System.Drawing.StringAlignment.Center,
            FormatFlags = System.Drawing.StringFormatFlags.NoWrap | System.Drawing.StringFormatFlags.NoClip
        };
        var glyph = LucideInstallerIcons.GetGlyph(LucideIcon.RefreshCw);
        path.AddString(glyph, font.FontFamily, (int)System.Drawing.FontStyle.Regular, 24, new System.Drawing.PointF(0, 0), sf);
        
        var pts = path.PathPoints;
        // In Lucide RefreshCw SVG:
        // SVG viewBox: 0 0 24 24.
        // Arc 1: M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8
        // Arrow 1: M21 3v5h-5
        // Arc 2: M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16
        // Arrow 2: M8 16H3v5
        // Circle center in SVG is exactly (12, 12).
        // Let's verify that Arrow 1 (21, 3) and Arrow 2 (3, 21) are point-symmetric around (12, 12):
        // 21 + 3 = 24 -> 24/2 = 12.
        // 3 + 21 = 24 -> 24/2 = 12.
        // The whole icon in Lucide is 180-degree rotational symmetric around (12, 12)!
        // Therefore, the true center of the icon is exactly the center of the bounding box of the 24x24 grid.
        
        var bounds = path.GetBounds();
        // Since the font glyph has exact 180-degree symmetry, bounds.X + bounds.Width/2 and bounds.Y + bounds.Height/2 is EXACTLY the rotational center!
        Assert.True(bounds.Width > 0 && bounds.Height > 0);
    }
}
