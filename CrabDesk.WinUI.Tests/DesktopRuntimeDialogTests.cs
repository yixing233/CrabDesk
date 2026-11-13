using System.Text.RegularExpressions;
using System.Windows.Media;
using CrabDesk.Runtime;
using CrabDesk.WinUI.Controls;
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
    public void WpfTablerFontContainsTheWarningGlyph()
    {
        var codePoint = char.ConvertToUtf32(
            TablerRuntimeIcons.GetGlyph(TablerRuntimeIcon.TriangleAlert),
            0);
        var containsGlyph = TablerRuntimeIcons.CreateWpfFontFamily()
            .GetTypefaces()
            .Any(typeface =>
                typeface.TryGetGlyphTypeface(out var glyphTypeface) &&
                glyphTypeface.CharacterToGlyphMap.ContainsKey(codePoint));

        Assert.True(containsGlyph);
    }

    [Fact]
    public void EveryRuntimeTablerIconExistsInTheBundledFont()
    {
        var glyphs = BundledFontGlyphs();
        var missing = Enum.GetValues<TablerRuntimeIcon>()
            .Where(icon => !glyphs.Contains(FirstCodePoint(TablerRuntimeIcons.GetGlyph(icon))))
            .Select(icon => icon.ToString())
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void EveryTablerIconReferencedByXamlExistsInTheBundledFont()
    {
        var glyphs = BundledFontGlyphs();
        var solution = FindSolutionDirectory();
        var missing = new List<string>();

        foreach (var file in Directory.EnumerateFiles(solution, "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"<(?:\w+):TablerIcon[^>]*?\sIcon=""(?<name>\w+)"""))
            {
                var name = match.Groups["name"].Value;
                if (!Enum.TryParse<TablerIconName>(name, out var icon) || icon == TablerIconName.None)
                {
                    missing.Add($"{Path.GetFileName(file)}: unknown icon {name}");
                    continue;
                }

                if (!glyphs.Contains(FirstCodePoint(TablerGlyphs.ToGlyph(icon))))
                {
                    missing.Add($"{Path.GetFileName(file)}: {name} has no glyph");
                }
            }
        }

        Assert.Empty(missing);
    }

    private static HashSet<int> BundledFontGlyphs()
    {
        var fontPath = Path.Combine(AppContext.BaseDirectory, "Assets", "tabler-icons.ttf");
        Assert.True(File.Exists(fontPath), $"Expected the bundled Tabler font at {fontPath}.");

        var typeface = new GlyphTypeface(new Uri(fontPath));
        Assert.Equal("tabler-icons", typeface.FamilyNames.Values.First());
        return typeface.CharacterToGlyphMap.Keys.ToHashSet();
    }

    private static int FirstCodePoint(string glyph) =>
        glyph.Length == 0 ? -1 : char.ConvertToUtf32(glyph, 0);

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
