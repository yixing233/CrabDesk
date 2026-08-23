using CrabDesk.Native;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class LayeredWindowPresenterCopyTests
{
    [Fact]
    public void PartialCopyPreservesPixelsOutsideDirtyRectangle()
    {
        var source = Enumerable.Range(1, 24).Select(value => (byte)value).ToArray();
        var destination = Enumerable.Repeat((byte)0xEE, 40).ToArray();

        LayeredWindowPresenter.CopyRectangleForTesting(
            source,
            sourceStride: 12,
            destination,
            destinationStride: 20,
            rowBytes: 8,
            rowCount: 2,
            destinationOffset: 4);

        Assert.Equal(source[..8], destination[4..12]);
        Assert.Equal(source[12..20], destination[24..32]);
        Assert.All(destination[..4], value => Assert.Equal(0xEE, value));
    }
}
