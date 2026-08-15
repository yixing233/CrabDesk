using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopHoverRenderStateTests
{
    [Fact]
    public void PublishKeepsOnlyTheNewestHoverKey()
    {
        var state = new DesktopHoverRenderState();

        Assert.True(state.Publish("one"));
        Assert.False(state.Publish("two"));
        Assert.True(state.TryTake(out var key));
        Assert.Equal("two", key);
        Assert.False(state.TryTake(out _));
    }

    [Fact]
    public void ClearingHoverPublishesNullAndCoalesces()
    {
        var state = new DesktopHoverRenderState();

        Assert.True(state.Publish("one"));
        Assert.False(state.Publish(null));
        Assert.True(state.TryTake(out var key));
        Assert.Null(key);
        Assert.False(state.TryTake(out _));
    }
}
