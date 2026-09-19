using System.Xml.Linq;
using Xunit;

namespace CrabDesk.WinUI.Tests;

/// <summary>
/// The border settings shipped once as a toggle with no renderer behind it.
/// These pin that every border control on the appearance page is actually bound
/// to its view model property, so the panel cannot silently lose a knob.
/// </summary>
public sealed class AppearanceBorderSettingsTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void AppearancePageExposesEveryBorderControl()
    {
        var document = LoadXaml("CrabDesk.WinUI", "Views", "AppearancePage.xaml");

        AssertBound(document, "ToggleSwitch", "IsOn", "ShowBorder");
        AssertBound(document, "Slider", "Value", "BorderWidth");
        AssertBound(document, "Slider", "Value", "BorderOpacity");
        AssertBound(document, "ToggleSwitch", "IsOn", "UseAutomaticBorderColor");
        AssertBound(document, "ColorPicker", "Color", "ManualBorderColor");
    }

    [Fact]
    public void BorderWidthSliderRangeMatchesTheSupportedClamp()
    {
        var document = LoadXaml("CrabDesk.WinUI", "Views", "AppearancePage.xaml");
        var slider = document
            .Descendants(Presentation + "Slider")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "BorderWidthSlider");

        // A slider range wider than the runtime clamp would let the user pick a
        // value that silently snaps back on save.
        Assert.Equal("1", (string?)slider.Attribute("Minimum"));
        Assert.Equal("6", (string?)slider.Attribute("Maximum"));
    }

    [Fact]
    public void BorderOpacitySliderRangeMatchesTheSupportedClamp()
    {
        var document = LoadXaml("CrabDesk.WinUI", "Views", "AppearancePage.xaml");
        var slider = document
            .Descendants(Presentation + "Slider")
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "BorderOpacitySlider");

        Assert.Equal("0", (string?)slider.Attribute("Minimum"));
        Assert.Equal("100", (string?)slider.Attribute("Maximum"));
    }

    [Fact]
    public void BorderControlsAreDisabledWhenTheBorderIsOff()
    {
        var document = LoadXaml("CrabDesk.WinUI", "Views", "AppearancePage.xaml");

        // Adjusting the width or color of a border that is not drawn is a dead
        // control, so they follow the visibility toggle.
        foreach (var name in new[] { "BorderWidthSlider", "BorderOpacitySlider" })
        {
            var slider = document
                .Descendants(Presentation + "Slider")
                .Single(element => (string?)element.Attribute(Xaml + "Name") == name);
            Assert.Equal("{Binding ShowBorder}", (string?)slider.Attribute("IsEnabled"));
        }
    }

    private static void AssertBound(
        XDocument document,
        string elementName,
        string attribute,
        string expectedBinding)
    {
        var match = document
            .Descendants(Presentation + elementName)
            .FirstOrDefault(element =>
                ((string?)element.Attribute(attribute))?.Contains(expectedBinding, StringComparison.Ordinal) == true);
        Assert.True(match is not null,
            $"No <{elementName}> binds {attribute} to {expectedBinding} on the appearance page.");
    }

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
