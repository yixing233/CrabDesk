using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopHitMaskPresentationTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void HitMaskPresentsOnlyForNewBitmapOrLostPresenter(
        bool bitmapPresented,
        bool presenterLost,
        bool expected) =>
        Assert.Equal(expected, DesktopBoxForm.ShouldPresentHitMask(bitmapPresented, presenterLost));
}
