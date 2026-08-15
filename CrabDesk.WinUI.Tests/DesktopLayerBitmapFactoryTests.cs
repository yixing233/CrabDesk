using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopLayerBitmapFactoryTests
{
    [Fact]
    public void CreateUsesLogical96DpiForManuallyScaledDesktopLayers()
    {
        using var bitmap = DesktopLayerBitmapFactory.Create(8, 8);

        Assert.Equal(96f, bitmap.HorizontalResolution);
        Assert.Equal(96f, bitmap.VerticalResolution);
    }
}
