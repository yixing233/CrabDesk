using CrabDesk.Native;

namespace CrabDesk.Tests;

public sealed class ShellIconProviderTests
{
    [Fact]
    public void ShellIconsRetainTransparentPixels()
    {
        var path = Path.Combine(Path.GetTempPath(), $"CrabDeskIcon-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "icon alpha test");
        var provider = new ShellIconProvider();
        try
        {
            var bitmap = provider.GetIcon(path, 48);

            Assert.NotNull(bitmap);
            var alphaValues = Enumerable.Range(0, bitmap!.Width)
                .SelectMany(x => Enumerable.Range(0, bitmap.Height)
                    .Select(y => bitmap.GetPixel(x, y).A));
            Assert.Contains(alphaValues, alpha => alpha < byte.MaxValue);
        }
        finally
        {
            provider.ClearCache();
            File.Delete(path);
        }
    }
}
