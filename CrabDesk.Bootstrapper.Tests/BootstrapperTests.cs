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
}
