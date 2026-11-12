using System.Drawing;
using System.Drawing.Drawing2D;
using CrabDesk.Core;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class WidgetColorAssetTests
{
    [Fact]
    public void EveryEmbeddedAssetHasMatchingSvgAndRendersWithColorAndTransparency()
    {
        var names = typeof(WidgetColorIcons).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("CrabDesk.Runtime.Assets.WidgetIcons.", StringComparison.Ordinal))
            .Select(name => name["CrabDesk.Runtime.Assets.WidgetIcons.".Length..^4]).Order().ToArray();
        Assert.Equal(55, names.Length);
        foreach (var kind in Enum.GetValues<DesktopWidgetKind>()) Assert.Contains(WidgetIconCatalog.GetName(kind), names);
        foreach (var name in names)
        {
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "WidgetIcons", name + ".svg")), name);
            foreach (var size in new[] { 20, 24, 32, 48, 64 })
            {
                using var image = new Bitmap(size, size);
                using var graphics = Graphics.FromImage(image);
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                WidgetColorIcons.Draw(graphics, name, new(0, 0, size, size));
                Assert.Equal(InterpolationMode.NearestNeighbor, graphics.InterpolationMode);
                Assert.Equal(0, image.GetPixel(0, 0).A);
                var painted = 0;
                var colored = 0;
                for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var pixel = image.GetPixel(x, y);
                    if (pixel.A > 0) painted++;
                    if (pixel.A > 100 && Math.Abs(pixel.R - pixel.B) > 15) colored++;
                }
                Assert.True(painted > size * size / 8, $"{name} at {size}px has no usable silhouette");
                Assert.True(colored > size, $"{name} at {size}px lost its palette");
            }
        }
    }

    [Fact]
    public void MissingFileOpacityPreservesOriginalPalette()
    {
        using var normal = new Bitmap(32, 32);
        using var faded = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(normal)) WidgetColorIcons.Draw(graphics, "file-generic", new(0, 0, 32, 32));
        using (var graphics = Graphics.FromImage(faded)) WidgetColorIcons.Draw(graphics, "file-generic", new(0, 0, 32, 32), .45f);
        var original = normal.GetPixel(8, 15);
        var missing = faded.GetPixel(8, 15);
        Assert.Equal(255, original.A);
        Assert.InRange(missing.A, 113, 116);
        Assert.InRange(Math.Abs(original.R - missing.R), 0, 2);
        Assert.InRange(Math.Abs(original.G - missing.G), 0, 2);
        Assert.InRange(Math.Abs(original.B - missing.B), 0, 2);
    }
}
