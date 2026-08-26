using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopBoxTransformCompletionTests
{
    [Fact]
    public void BoxTransformCompletionRestoresHoverOwnedHeaderActions()
    {
        var source = File.ReadAllText(Path.Combine(
            FindSolutionDirectory(),
            "CrabDesk.Runtime",
            "DesktopBoxForm.Input.cs"));
        var methodStart = source.IndexOf(
            "private void CompleteBoxTransform(",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private void UpdateMovingBox(",
            Math.Max(0, methodStart),
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        var settledFrameCommit = method.IndexOf("FlushTransformTrail();", StringComparison.Ordinal);
        var hoverReconcile = method.IndexOf("QueueHoverReconcile();", StringComparison.Ordinal);

        Assert.True(settledFrameCommit >= 0);
        Assert.True(hoverReconcile > settledFrameCommit);
    }

    private static string FindSolutionDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CrabDesk.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the CrabDesk solution directory.");
    }
}
