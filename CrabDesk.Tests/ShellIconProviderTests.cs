using CrabDesk.Native;
using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CrabDesk.Tests;

public sealed class ShellIconProviderTests
{
    [Theory]
    [InlineData("shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", true)]
    [InlineData("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", true)]
    [InlineData("shell:AppsFolder", false)]
    [InlineData("shell:::{invalid}", false)]
    [InlineData(@"C:\Desktop\folder", false)]
    public void RegisteredIconLookupOnlyAppliesToNamespaceObjects(string name, bool expected) =>
        Assert.Equal(expected, ShellIconProvider.TryGetNamespaceClsid(name, out _));

    [Theory]
    [InlineData("shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")]
    [InlineData("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")]
    public async Task ThisPcUsesItsRegisteredDesktopIconOnTheBackgroundLoader(string parsingName)
    {
        const string subkey = @"CLSID\{20D04FE0-3AEA-1069-A2D8-08002B30309D}\DefaultIcon";
        using var userKey = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\" + subkey);
        using var systemKey = Registry.ClassesRoot.OpenSubKey(subkey);
        var resource = (string)(userKey?.GetValue(null) ?? systemKey!.GetValue(null)!);
        var separator = resource.LastIndexOf(',');
        var path = Environment.ExpandEnvironmentVariables(resource[..separator].Trim().Trim('"'));
        var index = int.Parse(resource[(separator + 1)..]);
        Assert.Equal(0, SHDefExtractIcon(path, index, 0, out var handle, IntPtr.Zero, 57));
        using var expectedIcon = (Icon)Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        using var expected = expectedIcon.ToBitmap();
        var provider = new ShellIconProvider();
        try
        {
            var actual = await Task.Run(() => provider.GetIcon(parsingName, 57));
            Assert.NotNull(actual);
            Assert.Equal(expected.Size, actual!.Size);
            Assert.Equal(Pixels(expected), Pixels(actual));
        }
        finally { provider.ClearCache(); }
    }

    private static int[] Pixels(Bitmap bitmap) => Enumerable.Range(0, bitmap.Width)
        .SelectMany(x => Enumerable.Range(0, bitmap.Height).Select(y => bitmap.GetPixel(x, y).ToArgb())).ToArray();

    [DllImport("shell32.dll", EntryPoint = "SHDefExtractIconW", CharSet = CharSet.Unicode)]
    private static extern int SHDefExtractIcon(string path, int index, uint flags,
        out IntPtr largeIcon, IntPtr smallIcon, uint size);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [Fact]
    public void FailedShellLookupsAreNotCachedAsPermanentBlankIcons()
    {
        var missingPath = Path.Combine(
            Path.GetTempPath(),
            $"CrabDeskMissing-{Guid.NewGuid():N}",
            "missing.no-such-extension");
        var provider = new ShellIconProvider();

        Assert.Null(provider.GetIcon(missingPath, 48));
        Assert.Null(provider.GetIcon(missingPath, 48));

        var statistics = provider.GetCacheStatistics();
        Assert.Equal(0, statistics.Count);
        Assert.Equal(2, statistics.Misses);
    }

    [Fact]
    public void ShellIconsRetainTransparentPixels()
    {
        var path = Path.Combine(Path.GetTempPath(), $"CrabDeskIcon-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "icon alpha test");
        var provider = new ShellIconProvider();
        try
        {
            var bitmap = provider.GetIcon(path, 48);

            Assert.NotNull(bitmap);
            var alphaValues = Enumerable.Range(0, bitmap!.Width)
                .SelectMany(x => Enumerable.Range(0, bitmap.Height)
                    .Select(y => bitmap.GetPixel(x, y).A));
            Assert.Contains(alphaValues, alpha => alpha < byte.MaxValue);
        }
        finally
        {
            provider.ClearCache();
            File.Delete(path);
        }
    }

    [Fact]
    public void ShortcutIconsUseTheFullShellSurfaceWhenTheOverlayIsSuppressed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"CrabDeskShortcut-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var shortcutPath = Path.Combine(root, "cmd.lnk");
        dynamic? shell = null;
        dynamic? shortcut = null;
        var provider = new ShellIconProvider();
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
            shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            shortcut.Save();

            var icon = provider.GetIcon(shortcutPath, 96, includeShortcutOverlay: false);

            Assert.NotNull(icon);
            var bounds = AlphaBounds(icon!);
            Assert.True(bounds.Width >= 48 && bounds.Height >= 48,
                $"Shortcut artwork was unexpectedly tiny: {bounds.Width}x{bounds.Height}.");
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
            provider.ClearCache();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static Rectangle AlphaBounds(Bitmap bitmap)
    {
        var left = bitmap.Width;
        var top = bitmap.Height;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).A == 0) continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }
        return right < left || bottom < top
            ? Rectangle.Empty
            : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }
}
