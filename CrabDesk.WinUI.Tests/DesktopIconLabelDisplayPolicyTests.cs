using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopIconLabelDisplayPolicyTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void OnlySelectionExpandsTheDesktopLabel(
        bool isSelected,
        bool isHovered,
        bool expectedFullLabel)
    {
        Assert.Equal(
            expectedFullLabel,
            DesktopIconLabelDisplayPolicy.ShowsFullLabel(isSelected, isHovered));
    }
}
