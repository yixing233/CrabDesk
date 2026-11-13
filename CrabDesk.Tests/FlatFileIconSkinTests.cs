using System.Drawing;
using System.Drawing.Imaging;
using CrabDesk.Native;

namespace CrabDesk.Tests;

public sealed class FlatFileIconSkinTests
{
    private static readonly IconSkinOptions Flat = IconSkinOptions.Disabled with { Enabled = true };

    [Theory]
    [InlineData("report.pdf", FlatFileIconKind.Pdf, "PDF")]
    [InlineData("REPORT.DOCX", FlatFileIconKind.Document, "DOCX")]
    [InlineData("letter.doc", FlatFileIconKind.Document, "DOC")]
    [InlineData("letter.odt", FlatFileIconKind.Document, "ODT")]
    [InlineData("book.xlsx", FlatFileIconKind.Spreadsheet, "XLSX")]
    [InlineData("book.xls", FlatFileIconKind.Spreadsheet, "XLS")]
    [InlineData("data.csv", FlatFileIconKind.Spreadsheet, "CSV")]
    [InlineData("slides.pptx", FlatFileIconKind.Presentation, "PPTX")]
    [InlineData("slides.ppt", FlatFileIconKind.Presentation, "PPT")]
    [InlineData("notes.txt", FlatFileIconKind.Text, "TXT")]
    [InlineData("readme.md", FlatFileIconKind.Text, "MD")]
    [InlineData("backup.zip", FlatFileIconKind.Archive, "ZIP")]
    [InlineData("backup.7z", FlatFileIconKind.Archive, "7Z")]
    [InlineData("backup.tar.gz", FlatFileIconKind.Archive, "GZ")]
    [InlineData("backup.rar", FlatFileIconKind.Archive, "RAR")]
    [InlineData("photo.jpg", FlatFileIconKind.Image, "JPG")]
    [InlineData("photo.png", FlatFileIconKind.Image, "PNG")]
    [InlineData("photo.webp", FlatFileIconKind.Image, "WEBP")]
    [InlineData("photo.heic", FlatFileIconKind.Image, "HEIC")]
    [InlineData("photo.svg", FlatFileIconKind.Image, "SVG")]
    [InlineData("song.mp3", FlatFileIconKind.Audio, "MP3")]
    [InlineData("song.flac", FlatFileIconKind.Audio, "FLAC")]
    [InlineData("movie.mp4", FlatFileIconKind.Video, "MP4")]
    [InlineData("movie.mkv", FlatFileIconKind.Video, "MKV")]
    [InlineData("app.cs", FlatFileIconKind.Code, "CS")]
    [InlineData("config.json", FlatFileIconKind.Code, "JSON")]
    [InlineData("config.ini", FlatFileIconKind.Code, "INI")]
    [InlineData("custom.xyz", FlatFileIconKind.Generic, "XYZ")]
    [InlineData("README", FlatFileIconKind.Generic, "FILE")]
    [InlineData("custom.verylongextension", FlatFileIconKind.Generic, "FILE")]
    public void ExtensionsSelectTheirDocumentStyle(string path, FlatFileIconKind kind, string label)
    {
        var style = FlatFileIconStyle.FromPath(path);
        Assert.Equal(kind, style.Kind);
        Assert.Equal(label, style.Label);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(64)]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(256)]
    public void FileTypesRenderAsDocumentsAtEveryDesktopSize(int size)
    {
        using var source = new Bitmap(32, 32);
        foreach (var path in new[] { "a.pdf", "a.docx", "a.xlsx", "a.pptx", "a.txt", "a.zip", "a.png", "a.mp3", "a.mp4", "a.cs", "a.xyz", "README" })
        {
            using var icon = FlatIconSkin.Render(source, size, IconSkinKind.File, Flat, path);
            Assert.Equal(new Size(size, size), icon.Size);
            Assert.Equal(0, icon.GetPixel(0, 0).A);
            Assert.Equal(0, icon.GetPixel(0, size / 2).A);
            Assert.Equal(255, icon.GetPixel(size / 2, size / 5).A);
            Assert.True(icon.GetPixel(size / 2, size * 4 / 5).A > 240);
        }
    }

    [Fact]
    public void DocumentCategoriesHaveDistinctTypeColors()
    {
        using var source = new Bitmap(32, 32);
        var colors = new HashSet<int>();
        foreach (var path in new[] { "a.pdf", "a.docx", "a.xlsx", "a.pptx", "a.txt", "a.zip", "a.png", "a.mp3", "a.mp4", "a.cs" })
        {
            var style = FlatFileIconStyle.FromPath(path);
            using var icon = FlatIconSkin.Render(source, 96, IconSkinKind.File, Flat, path);
            Assert.Equal(style.Accent.ToArgb(), icon.GetPixel(23, 75).ToArgb());
            Assert.True(colors.Add(style.Accent.ToArgb()), $"Duplicate type color for {path}");
        }
    }

    [Theory]
    [InlineData(120, 40)]
    [InlineData(40, 120)]
    public void ImagePreviewsKeepTheirFullContentAndAspectRatio(int width, int height)
    {
        using var source = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(source))
        {
            graphics.Clear(Color.Magenta);
            using var brush = new SolidBrush(Color.Cyan);
            graphics.FillRectangle(brush, width / 3, height / 3, width / 3, height / 3);
        }
        using var icon = FlatIconSkin.Render(source, 96, IconSkinKind.File, Flat, "photo.png");
        var pixels = from x in Enumerable.Range(0, 96)
                     from y in Enumerable.Range(0, 65)
                     let color = icon.GetPixel(x, y)
                     where color.R > 200 && color.G < 70 && color.B > 200
                     select new Point(x, y);
        var points = pixels.ToArray();
        Assert.NotEmpty(points);
        var renderedWidth = points.Max(p => p.X) - points.Min(p => p.X) + 1;
        var renderedHeight = points.Max(p => p.Y) - points.Min(p => p.Y) + 1;
        Assert.InRange(renderedWidth / (double)renderedHeight, width / (double)height * 0.88, width / (double)height * 1.12);
        Assert.Equal(Color.Magenta.ToArgb(), source.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.Cyan.ToArgb(), source.GetPixel(width / 2, height / 2).ToArgb());
    }

    [Fact]
    public void CustomPaperColorsSupportLightAndDarkBackgrounds()
    {
        using var source = new Bitmap(32, 32);
        foreach (var color in new[] { Color.White, Color.FromArgb(36, 39, 45) })
        {
            using var icon = FlatIconSkin.Render(source, 96, IconSkinKind.File, Flat with { FolderPlate = color }, "a.pdf");
            Assert.Equal(color.ToArgb(), icon.GetPixel(20, 55).ToArgb());
            Assert.Equal(0, icon.GetPixel(0, 55).A);
            Assert.Equal(FlatFileIconStyle.FromPath("a.pdf").Accent.ToArgb(), icon.GetPixel(23, 75).ToArgb());
        }
    }

    [Theory]
    [InlineData("a.xlsx", 41, 36)]
    [InlineData("a.pptx", 48, 41)]
    public void ContentRemainsVisibleWhenPaperMatchesTheTypeColor(string path, int x, int y)
    {
        using var source = new Bitmap(32, 32);
        var paper = FlatFileIconStyle.FromPath(path).Accent;
        using var icon = FlatIconSkin.Render(source, 96, IconSkinKind.File, Flat with { FolderPlate = paper }, path);
        var ink = icon.GetPixel(x, y);

        Assert.True(Math.Abs(ink.R - paper.R) + Math.Abs(ink.G - paper.G) + Math.Abs(ink.B - paper.B) > 150);
    }
}
