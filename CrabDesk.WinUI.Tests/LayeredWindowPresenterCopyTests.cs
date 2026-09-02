using CrabDesk.Native;
using System.Drawing;
using System.Windows.Forms;
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

    [Fact]
    public void ChildLayeredDestinationUsesParentClientCoordinates()
    {
        using var parent = new Form
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-1200, 80),
            ClientSize = new Size(1600, 900)
        };
        using var child = new Panel
        {
            Location = new Point(120, 40),
            Size = new Size(400, 300)
        };
        parent.Controls.Add(child);
        _ = parent.Handle;
        _ = child.Handle;

        var expected = new Point(320, 180);
        var screenLocation = parent.PointToScreen(expected);

        Assert.True(LayeredWindowPresenter.TryResolveDestinationLocation(
            child.Handle,
            screenLocation,
            out var actual));
        Assert.Equal(expected, actual);
    }
}
