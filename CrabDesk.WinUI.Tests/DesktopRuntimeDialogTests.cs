using System.Text.RegularExpressions;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class DesktopRuntimeDialogTests
{
    [Fact]
    public void MessageDialogReservesTheFlexibleRowForItsBody()
    {
        var source = File.ReadAllText(Path.Combine(
            FindSolutionDirectory(),
            "CrabDesk.Runtime",
            "DesktopConfirmationDialog.cs"));
        var rowDefinitions = Regex.Matches(
                source,
                @"content\.RowDefinitions\.Add\((?<definition>[^\r\n]+)\);",
                RegexOptions.CultureInvariant)
            .Select(match => match.Groups["definition"].Value)
            .Take(3)
            .ToArray();

        Assert.Equal(3, rowDefinitions.Length);
        Assert.Contains("GridLength.Auto", rowDefinitions[0], StringComparison.Ordinal);
        Assert.Contains("GridUnitType.Star", rowDefinitions[1], StringComparison.Ordinal);
        Assert.Contains("GridLength.Auto", rowDefinitions[2], StringComparison.Ordinal);
    }

    [Fact]
    public void WpfLucideFontContainsTheWarningGlyph()
    {
        var codePoint = char.ConvertToUtf32(
            LucideRuntimeIcons.GetGlyph(LucideRuntimeIcon.TriangleAlert),
            0);
        var containsGlyph = LucideRuntimeIcons.CreateWpfFontFamily()
            .GetTypefaces()
            .Any(typeface =>
                typeface.TryGetGlyphTypeface(out var glyphTypeface) &&
                glyphTypeface.CharacterToGlyphMap.ContainsKey(codePoint));

        Assert.True(containsGlyph);
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
