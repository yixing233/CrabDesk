using System.Text.Json;
using CrabDesk.Native;

namespace CrabDesk.Tests;

public sealed class DesktopContextMenuPositionStoreTests
{
    [Fact]
    public void PositionCanBeWrittenReadAndConsumedOnce()
    {
        WithTemporaryDataDirectory(root =>
        {
            DesktopContextMenuPositionStore.Write(-800, 640);

            Assert.True(DesktopContextMenuPositionStore.TryRead(out var read));
            Assert.Equal(-800, read.X);
            Assert.Equal(640, read.Y);
            Assert.True(DesktopContextMenuPositionStore.TryConsume(out var consumed));
            Assert.Equal(read, consumed);
            Assert.False(DesktopContextMenuPositionStore.TryRead(out var ignored));
            Assert.False(File.Exists(Path.Combine(root, "context-menu-position.json")));
        });
    }

    [Fact]
    public void ExpiredPositionIsRejectedAndRemoved()
    {
        WithTemporaryDataDirectory(_ =>
        {
            var path = DesktopContextMenuPositionStore.FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var expired = new DesktopContextMenuPosition(
                100,
                200,
                DateTime.UtcNow.AddSeconds(-30).Ticks);
            File.WriteAllText(path, JsonSerializer.Serialize(expired));

            Assert.False(DesktopContextMenuPositionStore.TryRead(out var ignored));
            Assert.False(File.Exists(path));
        });
    }

    [Fact]
    public void InvalidPositionPayloadFallsBackWithoutThrowing()
    {
        WithTemporaryDataDirectory(_ =>
        {
            var path = DesktopContextMenuPositionStore.FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "not-json");

            Assert.False(DesktopContextMenuPositionStore.TryConsume(out var ignored));
            Assert.True(File.Exists(path));
        });
    }

    private static void WithTemporaryDataDirectory(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "CrabDesk.ContextMenuTests", Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("CRABDESK_DATA_DIR");
        Environment.SetEnvironmentVariable("CRABDESK_DATA_DIR", root);
        try
        {
            action(root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CRABDESK_DATA_DIR", previous);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
