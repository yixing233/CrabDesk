using System.Drawing;
using System.Drawing.Imaging;
using CrabDesk.Core;
using CrabDesk.Native;
using CrabDesk.Runtime;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class FlatIconSkinRuntimeTests
{
    [Theory]
    [InlineData(DesktopItemKind.Shortcut, "editor.lnk", IconSkinKind.Shortcut)]
    [InlineData(DesktopItemKind.Shortcut, "website.url", IconSkinKind.Shortcut)]
    [InlineData(DesktopItemKind.Folder, "Work", IconSkinKind.Folder)]
    [InlineData(DesktopItemKind.File, "notes.txt", IconSkinKind.File)]
    [InlineData(DesktopItemKind.File, "editor.EXE", IconSkinKind.App)]
    [InlineData(DesktopItemKind.Shell, "::{645FF040-5081-101B-9F08-00AA002F954E}", IconSkinKind.App)]
    public void ItemClassificationSelectsItsPlateAndShortcutMark(
        DesktopItemKind kind, string path, IconSkinKind expected)
    {
        var item = new DesktopItemRef
        {
            Key = new DesktopItemKey("path", path),
            DisplayName = path,
            ParsingName = path,
            Kind = kind
        };

        Assert.Equal(expected, DesktopBoxForm.IconSkinKindOf(item));
    }

    [Fact]
    public void DisabledAppearanceReturnsAnIndependentCopyOfTheShellImage()
    {
        var provider = new ShellIconProvider();
        try
        {
            var path = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var original = provider.GetIcon(path, 48);
            Assert.NotNull(original);
            using var copy = IconSkinRenderer.Load(provider, path, 48, IconSkinKind.Folder, IconSkinOptions.Disabled);
            Assert.NotNull(copy);
            Assert.NotSame(original, copy);
            Assert.Equal(original.Size, copy.Size);
            Assert.Equal(original.GetPixel(24, 24), copy.GetPixel(24, 24));
        }
        finally
        {
            provider.ClearCache();
        }
    }

    [Fact]
    public void RealShellIconsRenderAtDesktopSizes()
    {
        var provider = new ShellIconProvider();
        try
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var samples = new (string Label, string Path, IconSkinKind Kind)[]
            {
                ("Explorer", Path.Combine(windows, "explorer.exe"), IconSkinKind.App),
                ("Notepad", Path.Combine(windows, "System32", "notepad.exe"), IconSkinKind.App),
                ("Folder", windows, IconSkinKind.Folder),
                ("Document", Path.Combine(windows, "win.ini"), IconSkinKind.File),
                ("This PC", "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", IconSkinKind.App),
                ("Recycle Bin", "::{645FF040-5081-101B-9F08-00AA002F954E}", IconSkinKind.App),
                ("Shortcut", Path.Combine(windows, "explorer.exe"), IconSkinKind.Shortcut)
            };
            var options = IconSkinOptions.Disabled with { Enabled = true };
            using var sheet = new Bitmap(960, 540);
            using var graphics = Graphics.FromImage(sheet);
            using var headingFont = new Font("Segoe UI", 12);
            using var labelFont = new Font("Segoe UI", 10);
            graphics.Clear(Color.FromArgb(229, 233, 238));
            graphics.DrawString("Flat desktop icons", headingFont, Brushes.Black, 20, 12);

            foreach (var (size, row) in new[] { (32, 0), (48, 1), (96, 2) })
            {
                graphics.DrawString($"{size} px", labelFont, Brushes.Black, 20, 55 + row * 150);
                for (var index = 0; index < samples.Length; index++)
                {
                    var sample = samples[index];
                    using var icon = IconSkinRenderer.Load(provider, sample.Path, size, sample.Kind, options);
                    Assert.NotNull(icon);
                    Assert.Equal(new Size(size, size), icon.Size);
                    Assert.Equal(0, icon.GetPixel(0, 0).A);
                    Assert.Equal(255, icon.GetPixel(size / 2, size / 2).A);
                    var x = 30 + index * 132;
                    var y = 85 + row * 150;
                    graphics.DrawImageUnscaled(icon, x + (96 - size) / 2, y);
                    graphics.DrawString(sample.Label, labelFont, Brushes.Black, x, y + 102);
                }
            }
            sheet.Save(Path.Combine(Path.GetTempPath(), "CrabDesk-Flat-Icons.png"), ImageFormat.Png);
        }
        finally
        {
            provider.ClearCache();
        }
    }
}
