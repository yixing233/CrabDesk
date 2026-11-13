using System.Drawing;
using System.Drawing.Imaging;
using CrabDesk.Native;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class FlatFileIconRuntimeTests
{
    [Theory]
    [InlineData(".pdf")]
    [InlineData(".doc")]
    [InlineData(".docx")]
    [InlineData(".xls")]
    [InlineData(".xlsx")]
    [InlineData(".ppt")]
    [InlineData(".pptx")]
    [InlineData(".txt")]
    [InlineData(".md")]
    [InlineData(".zip")]
    [InlineData(".7z")]
    [InlineData(".rar")]
    [InlineData(".mp3")]
    [InlineData(".flac")]
    [InlineData(".mp4")]
    [InlineData(".mkv")]
    [InlineData(".cs")]
    [InlineData(".JSON")]
    [InlineData(".xyz")]
    [InlineData("")]
    public void FilesUseTheirTypeStyleThroughTheSharedRuntimeLoader(string extension)
    {
        using var fixture = new FileFixture();
        var path = fixture.File("sample" + extension);
        var options = IconSkinOptions.Disabled with { Enabled = true };
        var style = FlatFileIconStyle.FromPath(path);

        foreach (var size in new[] { 16, 32, 48, 96 })
        {
            using var icon = IconSkinRenderer.Load(fixture.Provider, path, size, IconSkinKind.File, options);
            Assert.NotNull(icon);
            Assert.Equal(new Size(size, size), icon.Size);
            Assert.Equal(0, icon.GetPixel(0, size / 2).A);
            Assert.Equal(style.Accent.ToArgb(), icon.GetPixel((int)(size * 0.24), (int)(size * 0.78)).ToArgb());
        }
    }

    [Theory]
    [InlineData(180, 60, ".png")]
    [InlineData(60, 180, ".png")]
    [InlineData(180, 60, ".jpg")]
    [InlineData(60, 180, ".bmp")]
    public void ImageFilesKeepTheirThumbnailProportionsThroughTheRuntimeLoader(int width, int height, string extension)
    {
        using var fixture = new FileFixture();
        var path = fixture.Image($"photo{extension}", width, height);
        var options = IconSkinOptions.Disabled with { Enabled = true };
        var original = fixture.Provider.GetIcon(path, 96);
        Assert.NotNull(original);
        Assert.True(width > height ? original.Width > original.Height : original.Height > original.Width,
            $"Expected a {width}x{height} thumbnail, received {original.Width}x{original.Height}");
        using var icon = IconSkinRenderer.Load(fixture.Provider, path, 96, IconSkinKind.File, options);
        Assert.NotNull(icon);
        var points = (from x in Enumerable.Range(0, 96)
                      from y in Enumerable.Range(0, 65)
                      let color = icon.GetPixel(x, y)
                      where color.R > 190 && color.G < 80 && color.B > 190
                      select new Point(x, y)).ToArray();
        Assert.NotEmpty(points);
        var renderedWidth = points.Max(p => p.X) - points.Min(p => p.X) + 1;
        var renderedHeight = points.Max(p => p.Y) - points.Min(p => p.Y) + 1;
        Assert.InRange(renderedWidth / (double)renderedHeight, width / (double)height * 0.80, width / (double)height * 1.20);

        using var disabled = IconSkinRenderer.Load(fixture.Provider, path, 96, IconSkinKind.File, IconSkinOptions.Disabled);
        Assert.NotNull(disabled);
        Assert.NotSame(original, disabled);
        Assert.Equal(original.Size, disabled.Size);
        Assert.Equal(original.GetPixel(original.Width / 2, original.Height / 2), disabled.GetPixel(disabled.Width / 2, disabled.Height / 2));
    }

    [Fact]
    public void FileAppearancePreviewCoversDesktopSizesAndLightAndDarkPaper()
    {
        using var fixture = new FileFixture();
        var paths = new[]
        {
            fixture.File("Report.pdf"), fixture.File("Letter.docx"), fixture.File("Budget.xlsx"),
            fixture.File("Slides.pptx"), fixture.File("Notes.txt"), fixture.File("Backup.zip"),
            fixture.Image("Landscape.png", 180, 60), fixture.Image("Portrait.jpg", 60, 180),
            fixture.File("Music.mp3"), fixture.File("Video.mp4"), fixture.File("Code.cs"),
            fixture.File("Custom.xyz")
        };
        var output = Path.Combine(Path.GetTempPath(), "CrabDesk-Flat-File-Icons.png");
        using var sheet = new Bitmap(1320, 750);
        using var graphics = Graphics.FromImage(sheet);
        using var font = new Font("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
        using var title = new Font("Segoe UI", 16, FontStyle.Regular, GraphicsUnit.Pixel);
        graphics.Clear(Color.FromArgb(230, 234, 239));
        graphics.DrawString("File icons", title, Brushes.Black, 20, 14);
        var options = IconSkinOptions.Disabled with { Enabled = true };
        foreach (var (size, y) in new[] { (16, 70), (32, 170), (48, 280), (96, 405) })
        {
            graphics.DrawString($"{size} px", font, Brushes.Black, 20, y - 22);
            for (var column = 0; column < paths.Length; column++)
            {
                using var icon = IconSkinRenderer.Load(fixture.Provider, paths[column], size, IconSkinKind.File, options);
                Assert.NotNull(icon);
                var x = 22 + column * 108;
                graphics.DrawImageUnscaled(icon, x + (96 - size) / 2, y);
                graphics.DrawString(Path.GetFileName(paths[column]), font, Brushes.Black, x, y + size + 8);
            }
        }

        graphics.FillRectangle(Brushes.DimGray, 0, 555, sheet.Width, 195);
        graphics.DrawString("96 px / dark paper", font, Brushes.White, 20, 565);
        for (var column = 0; column < paths.Length; column++)
        {
            using var icon = IconSkinRenderer.Load(fixture.Provider, paths[column], 96, IconSkinKind.File,
                options with { FolderPlate = Color.FromArgb(36, 39, 45) });
            Assert.NotNull(icon);
            var x = 22 + column * 108;
            graphics.DrawImageUnscaled(icon, x, 600);
            graphics.DrawString(Path.GetFileName(paths[column]), font, Brushes.White, x, 705);
        }
        sheet.Save(output, ImageFormat.Png);
    }

    private sealed class FileFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"CrabDesk-File-Icons-{Guid.NewGuid():N}");
        internal ShellIconProvider Provider { get; } = new();

        internal FileFixture() => Directory.CreateDirectory(_root);

        internal string File(string name)
        {
            var path = Path.Combine(_root, name);
            // Non-image requests use IconOnly; their content is not decoded.
            System.IO.File.WriteAllText(path, "CrabDesk file association icon fixture.");
            return path;
        }

        internal string Image(string name, int width, int height)
        {
            var path = Path.Combine(_root, name);
            using var bitmap = new Bitmap(width, height);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Magenta);
                graphics.FillRectangle(Brushes.Cyan, width / 3, height / 3, width / 3, height / 3);
            }
            var format = Path.GetExtension(path) switch
            {
                ".jpg" => ImageFormat.Jpeg,
                ".bmp" => ImageFormat.Bmp,
                _ => ImageFormat.Png
            };
            bitmap.Save(path, format);
            return path;
        }

        public void Dispose()
        {
            Provider.ClearCache();
            Directory.Delete(_root, true);
        }
    }
}
