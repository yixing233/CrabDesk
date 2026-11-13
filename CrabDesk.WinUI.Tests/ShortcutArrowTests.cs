using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using CrabDesk.Core;
using CrabDesk.Native;
using CrabDesk.Runtime;
using CrabDesk.WinUI.Services;
using CrabDesk.WinUI.ViewModels;
using Moq;
using Xunit;

namespace CrabDesk.WinUI.Tests;

public sealed class ShortcutArrowTests
{
    [Theory]
    [InlineData("app")]
    [InlineData("folder")]
    [InlineData("file")]
    [InlineData("custom")]
    [InlineData("url")]
    public void RealShortcutsHonorTheArrowSettingWithAndWithoutTheFlatSkin(string targetKind)
    {
        using var fixture = new ShortcutFixture();
        var path = fixture.Create(targetKind);
        var provider = new ShellIconProvider();
        try
        {
            foreach (var size in new[] { 16, 32, 48, 96 })
            {
                var clean = provider.GetIcon(path, size, false);
                var original = provider.GetIcon(path, size);
                Assert.NotNull(clean);
                Assert.NotNull(original);
                var before = Pixels(original);
                foreach (var enabled in new[] { false, true })
                {
                    var hiddenOptions = IconSkinOptions.Disabled with { Enabled = enabled, ShowShortcutArrow = false };
                    using var hidden = IconSkinRenderer.Load(provider, path, size, IconSkinKind.Shortcut, hiddenOptions);
                    using var shown = IconSkinRenderer.Load(provider, path, size, IconSkinKind.Shortcut,
                        hiddenOptions with { ShowShortcutArrow = true });
                    Assert.NotNull(hidden);
                    Assert.NotNull(shown);
                    Assert.Equal(new Size(size, size), hidden.Size);
                    using var expected = enabled
                        ? FlatIconSkin.Render(clean, size, IconSkinKind.App, hiddenOptions)
                        : (Bitmap)clean.Clone();
                    Assert.Equal(Pixels(expected), Pixels(hidden));
                    if (size >= 24) Assert.NotEqual(Pixels(hidden), Pixels(shown));
                }
                Assert.Same(clean, provider.GetIcon(path, size, false));
                Assert.Equal(before, Pixels(original));
                if (targetKind is "custom" or "url")
                {
                    var bluePixels = Pixels(clean).Count(pixel => Color.FromArgb(pixel).B > 200
                        && Color.FromArgb(pixel).R is > 70 and < 130);
                    Assert.True(bluePixels > size * size / 3,
                        $"The custom shortcut artwork was lost at {size}px ({bluePixels} blue pixels).");
                }
            }
        }
        finally
        {
            provider.ClearCache();
        }
    }

    [Fact]
    public void ArrowSwitchRefreshesTheWorkspaceAndSchedulesPersistenceWithoutChangingTheSkin()
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var runtime = (CrabDeskRuntime)RuntimeHelpers.GetUninitializedObject(typeof(CrabDeskRuntime));
        var state = new CrabDeskState();
        typeof(CrabDeskRuntime).GetField("<State>k__BackingField", hidden)!.SetValue(runtime, state);
        using var timer = new RuntimeTimer(TimeSpan.FromHours(1), false, _ => { }, () => { });
        typeof(CrabDeskRuntime).GetField("_saveTimer", hidden)!.SetValue(runtime, timer);
        var changed = 0;
        runtime.Changed += (_, _) => changed++;
        var shownStamp = runtime.ResolveIconSkin().CacheStamp();

        runtime.SetShowShortcutArrow(false);

        Assert.False(state.Settings.Appearance.ShowShortcutArrow);
        Assert.False(runtime.ResolveIconSkin().ShowShortcutArrow);
        Assert.False(state.Settings.Appearance.IconSkinEnabled);
        Assert.NotEqual(shownStamp, runtime.ResolveIconSkin().CacheStamp());
        Assert.Equal(1, changed);
        Assert.NotNull(typeof(RuntimeTimer).GetField("_timer", hidden)!.GetValue(timer));
        runtime.SetShowShortcutArrow(false);
        Assert.Equal(1, changed);
        runtime.SetShowShortcutArrow(true);
        Assert.True(runtime.ResolveIconSkin().ShowShortcutArrow);
        Assert.Equal(shownStamp, runtime.ResolveIconSkin().CacheStamp());
        Assert.Equal(2, changed);
    }

    [Fact]
    public void ViewModelUpdatesTheArrowAndBadgeColorAvailability()
    {
        var state = new CrabDeskState();
        state.Settings.Appearance.IconSkinEnabled = true;
        var service = new Mock<ICrabDeskService>();
        service.SetupGet(item => item.State).Returns(state);
        service.SetupGet(item => item.Boxes).Returns(Array.Empty<DesktopBox>());
        service.Setup(item => item.SetShowShortcutArrow(It.IsAny<bool>())).Callback<bool>(show =>
        {
            state.Settings.Appearance.ShowShortcutArrow = show;
            service.Raise(item => item.Changed += null, EventArgs.Empty);
        });
        var viewModel = new AppearanceViewModel(service.Object, Mock.Of<IFontCatalogService>());
        var refreshed = false;
        viewModel.PropertyChanged += (_, args) => refreshed |= string.IsNullOrEmpty(args.PropertyName);
        Assert.True(viewModel.CanEditShortcutBadgeColor);

        viewModel.ShowShortcutArrow = false;

        Assert.False(viewModel.ShowShortcutArrow);
        Assert.False(viewModel.CanEditShortcutBadgeColor);
        Assert.True(refreshed);
        viewModel.ShowShortcutArrow = true;
        Assert.True(viewModel.CanEditShortcutBadgeColor);
        state.Settings.Appearance.IconSkinEnabled = false;
        Assert.False(viewModel.CanEditShortcutBadgeColor);
    }

    [Fact]
    public void AppearancePageExposesAnIndependentTwoWayArrowSwitch()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CrabDesk.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var page = XDocument.Load(Path.Combine(directory.FullName, "CrabDesk.WinUI", "Views", "AppearancePage.xaml"));
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var toggle = page.Descendants(ui + "ToggleSwitch").Single(item =>
            (string?)item.Attribute("IsOn") == "{Binding ShowShortcutArrow, Mode=TwoWay}");
        Assert.Null(toggle.Attribute("IsEnabled"));
        var badgeColor = page.Descendants(ui + "Button").Single(item =>
            (string?)item.Attribute("AutomationProperties.Name") == "选择快捷方式角标底色");
        Assert.Equal("{Binding CanEditShortcutBadgeColor}", (string?)badgeColor.Attribute("IsEnabled"));
    }

    [Fact]
    public void ShortcutArrowPreviewShowsBothStylesAndStates()
    {
        using var fixture = new ShortcutFixture();
        var path = fixture.Create("custom");
        var provider = new ShellIconProvider();
        try
        {
            using var sheet = new Bitmap(640, 270);
            using var graphics = Graphics.FromImage(sheet);
            using var font = new Font("Segoe UI", 15, FontStyle.Regular, GraphicsUnit.Pixel);
            graphics.Clear(Color.FromArgb(230, 233, 238));
            for (var column = 0; column < 4; column++)
            {
                var options = IconSkinOptions.Disabled with
                {
                    Enabled = column >= 2,
                    ShowShortcutArrow = column % 2 == 0
                };
                graphics.DrawString($"{(options.Enabled ? "Flat" : "Original")} / {(options.ShowShortcutArrow ? "On" : "Off")}",
                    font, Brushes.Black, column * 160 + 14, 15);
                using var large = IconSkinRenderer.Load(provider, path, 96, IconSkinKind.Shortcut, options);
                using var small = IconSkinRenderer.Load(provider, path, 32, IconSkinKind.Shortcut, options);
                Assert.NotNull(large);
                Assert.NotNull(small);
                graphics.DrawImageUnscaled(large, column * 160 + 32, 60);
                graphics.DrawImageUnscaled(small, column * 160 + 64, 200);
            }
            sheet.Save(Path.Combine(Path.GetTempPath(), "CrabDesk-Shortcut-Arrows.png"), ImageFormat.Png);
        }
        finally
        {
            provider.ClearCache();
        }
    }

    private static int[] Pixels(Bitmap image) => Enumerable.Range(0, image.Height)
        .SelectMany(y => Enumerable.Range(0, image.Width).Select(x => image.GetPixel(x, y).ToArgb())).ToArray();

    private sealed class ShortcutFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"CrabDesk-Shortcuts-{Guid.NewGuid():N}");
        private readonly string _iconPath;

        internal ShortcutFixture()
        {
            Directory.CreateDirectory(_root);
            _iconPath = Path.Combine(_root, "custom.ico");
            using var artwork = new Bitmap(256, 256);
            using (var graphics = Graphics.FromImage(artwork))
            {
                graphics.Clear(Color.Transparent);
                using var brush = new SolidBrush(Color.CornflowerBlue);
                graphics.FillRectangle(brush, 16, 16, 224, 224);
                graphics.FillEllipse(Brushes.White, 80, 80, 96, 96);
            }
            using var png = new MemoryStream();
            artwork.Save(png, ImageFormat.Png);
            using var stream = File.Create(_iconPath);
            using var writer = new BinaryWriter(stream);
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)1);
            writer.Write(new byte[] { 0, 0, 0, 0 });
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write((uint)png.Length);
            writer.Write((uint)22);
            writer.Write(png.ToArray());
        }

        internal string Create(string targetKind)
        {
            if (targetKind == "url")
            {
                var urlPath = Path.Combine(_root, "website.url");
                File.WriteAllText(urlPath,
                    $"[InternetShortcut]\r\nURL=https://example.com/\r\nIconFile={_iconPath}\r\nIconIndex=0\r\n");
                return urlPath;
            }
            var path = Path.Combine(_root, $"{targetKind}.lnk");
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
            dynamic shortcut = shell.CreateShortcut(path);
            try
            {
                var target = targetKind switch
                {
                    "folder" => _root,
                    "file" => Path.Combine(_root, "notes.txt"),
                    _ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")
                };
                if (targetKind == "file") File.WriteAllText(target, "Shortcut target.");
                shortcut.TargetPath = target;
                if (targetKind == "custom") shortcut.IconLocation = _iconPath + ",0";
                shortcut.Save();
                return path;
            }
            finally
            {
                Marshal.FinalReleaseComObject(shortcut);
                Marshal.FinalReleaseComObject(shell);
            }
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
