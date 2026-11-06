using System.Runtime.InteropServices;
using CrabDesk.Core;
using CrabDesk.Native;
using Xunit;
using DisplayPath = CrabDesk.Native.ActiveDisplayPaths.PathInfo;

namespace CrabDesk.WinUI.Tests;

public sealed class DisplayControlTests
{
    [Theory]
    [InlineData(ControlDisplayMode.Internal, 0x81u)]
    [InlineData(ControlDisplayMode.Clone, 0x82u)]
    [InlineData(ControlDisplayMode.Extend, 0x84u)]
    [InlineData(ControlDisplayMode.External, 0x88u)]
    public void ProjectionAppliesTheRequestedWindowsTopology(ControlDisplayMode mode, uint expectedFlags)
    {
        uint? applied = null;
        var result = WindowsDisplayControl.ApplyMode(mode, flags => { applied = flags; return 0; });
        Assert.True(result.Succeeded);
        Assert.Equal(expectedFlags, applied);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(50)]
    [InlineData(87)]
    [InlineData(1610)]
    [InlineData(31)]
    public void NativeFailureIsReportedWithoutClaimingTheModeChanged(int status)
    {
        var result = WindowsDisplayControl.ApplyMode(ControlDisplayMode.Clone, _ => status);
        Assert.False(result.Succeeded);
        Assert.Contains(status.ToString(), result.Error);
    }

    [Fact]
    public void InvalidModeNeverCallsWindows()
    {
        var called = false;
        var result = WindowsDisplayControl.ApplyMode((ControlDisplayMode)99, _ => { called = true; return 0; });
        Assert.False(result.Succeeded);
        Assert.False(called);
    }

    [Fact]
    public void DisplayCountIncludesDisabledTargetsAndDeduplicatesTheirPossibleRoutes()
    {
        var internalPath = Path(0, 0, true, 11);
        var snapshot = WindowsDisplayControl.DescribeTopology(
            [internalPath, Path(0, 1, false), Path(1, 1, false), Path(1, 2, false, available: false)],
            [internalPath], 1);
        Assert.Equal(new ControlDisplaySnapshot(ControlDisplayMode.Internal, 2), snapshot);
    }

    [Theory]
    [InlineData(1u, ControlDisplayMode.Internal)]
    [InlineData(8u, ControlDisplayMode.External)]
    public void SingleDesktopDisplayUsesTheMatchingWindowsTopology(uint topology, ControlDisplayMode mode)
    {
        var path = Path(0, 1, true);
        Assert.Equal(mode, WindowsDisplayControl.DescribeTopology([path], [path], topology).Mode);
    }

    [Theory]
    [InlineData(0u, ControlDisplayMode.Clone)]
    [InlineData(1u, ControlDisplayMode.Extend)]
    public void ActivePathsOverrideAStaleSavedTopology(uint secondSource, ControlDisplayMode mode)
    {
        var first = Path(0, 0, true, 11);
        var snapshot = WindowsDisplayControl.DescribeTopology([first, Path(secondSource, 1, true)], [first], 1);
        Assert.Equal(mode, snapshot.Mode);
        Assert.Equal(2, snapshot.DisplayCount);
    }

    [Fact]
    public void SourceIdsOnDifferentAdaptersAreNotMistakenForCloning()
    {
        var first = Path(0, 0, true);
        var second = Path(0, 0, true);
        second.Source.Adapter.Low = 2;
        second.Target.Adapter.Low = 2;
        var snapshot = WindowsDisplayControl.DescribeTopology([first, second], [], 0);
        Assert.Equal(ControlDisplayMode.Extend, snapshot.Mode);
        Assert.Equal(2, snapshot.DisplayCount);
    }

    [Fact]
    public void DisconnectedActivePathsAreNotReportedAsConnectedDisplays()
    {
        var path = Path(0, 0, true, available: false);
        Assert.Equal(new ControlDisplaySnapshot(null, 0), WindowsDisplayControl.DescribeTopology([path], [path], 1));
    }

    [Fact]
    public void ExternalOnlyCanBeRecognizedWhenAnEmbeddedDisplayIsDisabled()
    {
        var snapshot = WindowsDisplayControl.DescribeTopology([Path(0, 0, false, 11), Path(1, 1, true)], [], 0);
        Assert.Equal(new ControlDisplaySnapshot(ControlDisplayMode.External, 2), snapshot);
    }

    [Fact]
    public void AmbiguousSingleDesktopDisplayDoesNotInventAProjectionMode()
    {
        Assert.Null(WindowsDisplayControl.DescribeTopology([Path(0, 0, true)], [], 0).Mode);
    }

    [Fact]
    public void CastShortcutHasTheWindowsInputLayoutAndReleasesBothKeys()
    {
        // Inspect the input data only; never inject keys or open Cast in tests.
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, Marshal.SizeOf<WindowsDisplayControl.ShortcutInput>());
        var inputs = WindowsDisplayControl.CreateCastShortcut();
        Assert.All(inputs, input => Assert.Equal(1u, input.Type));
        Assert.Equal(new ushort[] { 0x5B, 0x4B, 0x4B, 0x5B }, inputs.Select(input => input.Data.Keyboard.VirtualKey));
        Assert.Equal(new uint[] { 0, 0, 2, 2 }, inputs.Select(input => input.Data.Keyboard.Flags));
        Assert.Equal("ms-settings:display", WindowsDisplayControl.SettingsUri);
    }

    private static DisplayPath Path(uint source, uint target, bool active, uint technology = 5, bool available = true) => new()
    {
        Source = new() { Adapter = new() { Low = 1 }, Id = source },
        Target = new() { Adapter = new() { Low = 1 }, Id = target, Technology = technology, Available = available ? 1 : 0 },
        Flags = active ? 1u : 0u
    };
}
