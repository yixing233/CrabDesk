using System.Xml.Linq;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class BoxesPageLayoutTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void BoxesPageExposesEditableDipGeometry()
    {
        var document = LoadXaml("CrabDesk.WinUI", "Views", "BoxesPage.xaml");
        var numberBoxes = document.Descendants(Presentation + "NumberBox").ToArray();

        Assert.Contains(numberBoxes, item => HasBinding(item, "Value", "PositionX"));
        Assert.Contains(numberBoxes, item => HasBinding(item, "Value", "PositionY"));
        Assert.Contains(numberBoxes, item => HasBinding(item, "Value", "Width"));
        Assert.Contains(numberBoxes, item => HasBinding(item, "Value", "Height"));
        Assert.All(numberBoxes, item =>
            Assert.Equal("{Binding HasSelection}", (string?)item.Attribute("IsEnabled")));
        Assert.Contains(
            document.Descendants().SelectMany(element => element.Attributes()),
            attribute => attribute.Value.Contains("DIP", StringComparison.Ordinal));
    }

    [Fact]
    public void BoxesPageDetailsAreIndependentlyScrollable()
    {
        var document = LoadXaml("CrabDesk.WinUI", "Views", "BoxesPage.xaml");
        var scrollViewer = document.Descendants(Presentation + "ScrollViewer")
            .SingleOrDefault(item => (string?)item.Attribute("Grid.Column") == "1");

        Assert.NotNull(scrollViewer);
        Assert.Equal("Auto", (string?)scrollViewer!.Attribute("VerticalScrollBarVisibility"));
        Assert.Equal("Disabled", (string?)scrollViewer.Attribute("HorizontalScrollBarVisibility"));
    }

    [Fact]
    public void BoxesPageColumnsFillAvailableHeight()
    {
        var document = LoadXaml("CrabDesk.WinUI", "Views", "BoxesPage.xaml");
        var boxList = document.Descendants(Presentation + "Border")
            .Single(item => item.Descendants(Presentation + "ListView").Any());

        Assert.Equal("Stretch", (string?)boxList.Attribute("VerticalAlignment"));
        Assert.Null(boxList.Attribute("MaxHeight"));
    }

    [Fact]
    public void BoxesPageAllowsNegativePositionAndPositiveSizeRanges()
    {
        var document = LoadXaml("CrabDesk.WinUI", "Views", "BoxesPage.xaml");
        var numberBoxes = document.Descendants(Presentation + "NumberBox").ToArray();
        var positions = numberBoxes.Where(item =>
            ((string?)item.Attribute("Value"))?.Contains("Position", StringComparison.Ordinal) == true);
        var sizes = numberBoxes.Where(item =>
            ((string?)item.Attribute("Value")) is "{Binding Width, Mode=TwoWay}" or "{Binding Height, Mode=TwoWay}");

        Assert.Equal(2, positions.Count());
        Assert.All(positions, item => Assert.Equal("-100000", (string?)item.Attribute("Minimum")));
        Assert.Equal(2, sizes.Count());
        Assert.All(sizes, item => Assert.Equal("1", (string?)item.Attribute("Minimum")));
    }

    private static bool HasBinding(XElement element, string attribute, string property) =>
        ((string?)element.Attribute(attribute))?.Contains(property, StringComparison.Ordinal) == true;

    private static XDocument LoadXaml(params string[] pathParts) =>
        XDocument.Load(Path.Combine([FindSolutionDirectory(), .. pathParts]));

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
        throw new InvalidOperationException("CrabDesk.sln not found.");
    }
}
