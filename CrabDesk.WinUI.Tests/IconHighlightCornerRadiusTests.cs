using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Xml.Linq;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class IconHighlightCornerRadiusTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HighlightCanRenderSquareCornersAndRoundedCorners(bool boxItems)
    {
        var type = boxItems ? typeof(DesktopBoxForm) : typeof(DesktopIconSurface);
        var createPath = type.GetMethod("RoundedRectangle", BindingFlags.Static | BindingFlags.NonPublic)!;
        using var square = Render(createPath, 0);
        using var rounded = Render(createPath, 20);

        Assert.Equal(255, square.GetPixel(5, 5).A);
        Assert.Equal(0, rounded.GetPixel(5, 5).A);
        Assert.Equal(square.GetPixel(24, 24), rounded.GetPixel(24, 24));
    }

    [Fact]
    public void SharedHighlightRadiusBelongsToIconsAndStaysAvailableWithHoverDisabled()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CrabDesk.sln")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        var document = XDocument.Load(Path.Combine(directory!.FullName, "CrabDesk.WinUI", "Views", "AppearancePage.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var slider = Assert.Single(document.Descendants(presentation + "Slider").Where(element =>
            (string?)element.Attribute("Value") == "{Binding IconHighlightCornerRadius, Mode=TwoWay}"));

        Assert.Equal("0", (string?)slider.Attribute("Minimum"));
        Assert.Equal("20", (string?)slider.Attribute("Maximum"));
        Assert.Null(slider.Attribute("IsEnabled"));
        Assert.Contains(slider.Ancestors(), element => (string?)element.Attribute(xaml + "Name") == "PanelIconsAndLabels");
    }

    private static Bitmap Render(MethodInfo createPath, float radius)
    {
        var bitmap = new Bitmap(48, 48);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        using var path = (GraphicsPath)createPath.Invoke(null, [new RectangleF(4, 4, 40, 40), radius])!;
        graphics.FillPath(Brushes.White, path);
        return bitmap;
    }
}
