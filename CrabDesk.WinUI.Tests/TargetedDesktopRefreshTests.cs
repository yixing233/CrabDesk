using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class TargetedDesktopRefreshTests
{
    [Fact]
    public void ParentDirectoryWatcherEventsMatchTheMovedChildPath()
    {
        var directory = Path.GetFullPath(Path.Combine("desktop", "folder"));
        var movedFile = Path.Combine(directory, "item.txt");

        Assert.True(CrabDeskRuntime.PathsOverlapForTargetedDesktopRefresh(
            movedFile,
            directory));
    }

    [Fact]
    public void SiblingWatcherEventsDoNotMatchTheMovedPath()
    {
        var directory = Path.GetFullPath(Path.Combine("desktop", "folder"));

        Assert.False(CrabDeskRuntime.PathsOverlapForTargetedDesktopRefresh(
            Path.Combine(directory, "item.txt"),
            Path.Combine(directory, "other.txt")));
    }
}
