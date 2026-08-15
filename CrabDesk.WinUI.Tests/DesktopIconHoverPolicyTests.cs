using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopIconHoverPolicyTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void InteractiveBoxSuppressesDesktopIconHover(bool pointerOverBox, bool expected)
    {
        Assert.Equal(expected, DesktopIconHoverPolicy.CanHoverDesktopIcon(pointerOverBox));
    }
}
