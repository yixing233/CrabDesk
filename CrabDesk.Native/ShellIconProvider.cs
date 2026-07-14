using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace CrabDesk.Native;

public sealed record ShellImageCacheStatistics(
    int Count,
    long Hits,
    long Misses,
    long Evictions);

public sealed class ShellIconProvider
{
    private const int DefaultCacheCapacity = 512;
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint ShgfiPidl = 0x000000008;
    private const uint StgMedium = 0;
    private readonly object _cacheLock = new();
    private readonly Dictionary<CacheKey, CacheEntry> _cache = [];
    private readonly int _cacheCapacity;
    private long _accessSequence;
    private long _hits;
    private long _misses;
    private long _evictions;

    public ShellIconProvider(int cacheCapacity = DefaultCacheCapacity)
    {
        _cacheCapacity = Math.Clamp(cacheCapacity, 8, 4096);
    }

    public BitmapSource? GetIcon(string parsingName) => GetIcon(parsingName, 48);

    public BitmapSource? GetIcon(string parsingName, int pixelSize)
    {
        var key = CreateCacheKey(parsingName, pixelSize);
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                cached.LastAccess = ++_accessSequence;
                _hits++;
                return cached.Image;
            }
            _misses++;
        }

        var image = LoadImage(key.ParsingName, key.PixelSize);
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                cached.LastAccess = ++_accessSequence;
                return cached.Image;
            }
            _cache[key] = new CacheEntry(image, ++_accessSequence);
            TrimCache();
            return image;
        }
    }

    public int ClearCache()
    {
        lock (_cacheLock)
        {
            var count = _cache.Count;
            _cache.Clear();
            return count;
        }
    }

    public ShellImageCacheStatistics GetCacheStatistics()
    {
        lock (_cacheLock)
        {
            return new ShellImageCacheStatistics(_cache.Count, _hits, _misses, _evictions);
        }
    }

    private void TrimCache()
    {
        while (_cache.Count > _cacheCapacity)
        {
            var oldest = _cache.MinBy(entry => entry.Value.LastAccess).Key;
            _cache.Remove(oldest);
            _evictions++;
        }
    }

    private static CacheKey CreateCacheKey(string parsingName, int pixelSize)
    {
        var normalizedName = parsingName;
        long modifiedTicks = 0;
        long length = 0;
        try
        {
            if (File.Exists(parsingName))
            {
                var info = new FileInfo(parsingName);
                normalizedName = info.FullName;
                modifiedTicks = info.LastWriteTimeUtc.Ticks;
                length = info.Length;
            }
            else if (Directory.Exists(parsingName))
            {
                var info = new DirectoryInfo(parsingName);
                normalizedName = info.FullName;
                modifiedTicks = info.LastWriteTimeUtc.Ticks;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return new CacheKey(normalizedName, Math.Clamp(pixelSize, 16, 256), modifiedTicks, length);
    }

    private static BitmapSource? LoadImage(string parsingName, int pixelSize) =>
        LoadShellItemImage(parsingName, pixelSize) ?? LoadLegacyIcon(parsingName);

    private static BitmapSource? LoadShellItemImage(string parsingName, int pixelSize)
    {
        var interfaceId = typeof(IShellItemImageFactory).GUID;
        var result = SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref interfaceId, out var factory);
        if (result != 0 || factory is null)
        {
            return null;
        }

        IntPtr bitmapHandle = IntPtr.Zero;
        try
        {
            result = factory.GetImage(
                new NativeSize(pixelSize, pixelSize),
                ShellItemImageFlags.BiggerSizeOk,
                out bitmapHandle);
            if (result != 0 || bitmapHandle == IntPtr.Zero)
            {
                return null;
            }
            var image = Imaging.CreateBitmapSourceFromHBitmap(
                bitmapHandle,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(pixelSize, pixelSize));
            image.Freeze();
            return image;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            if (bitmapHandle != IntPtr.Zero)
            {
                DeleteObject(bitmapHandle);
            }
            Marshal.FinalReleaseComObject(factory);
        }
    }

    private static BitmapSource? LoadLegacyIcon(string parsingName)
    {
        IntPtr iconHandle = IntPtr.Zero;
        IntPtr pidl = IntPtr.Zero;
        try
        {
            ShFileInfo info;
            if (parsingName.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                if (SHParseDisplayName(parsingName, IntPtr.Zero, out pidl, StgMedium, out _) != 0)
                {
                    return null;
                }
                SHGetFileInfo(pidl, 0, out info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | ShgfiLargeIcon | ShgfiPidl);
            }
            else
            {
                SHGetFileInfo(parsingName, 0, out info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | ShgfiLargeIcon);
            }

            iconHandle = info.Icon;
            if (iconHandle == IntPtr.Zero)
            {
                return null;
            }
            var image = Imaging.CreateBitmapSourceFromHIcon(
                iconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally
        {
            if (iconHandle != IntPtr.Zero)
            {
                DestroyIcon(iconHandle);
            }
            if (pidl != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string path,
        IntPtr bindingContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        string name,
        IntPtr bindingContext,
        out IntPtr pidl,
        uint attributesIn,
        out uint attributesOut);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        out ShFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("shell32.dll")]
    private static extern IntPtr SHGetFileInfo(
        IntPtr pidl,
        uint fileAttributes,
        out ShFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, ShellItemImageFlags flags, out IntPtr bitmapHandle);
    }

    [Flags]
    private enum ShellItemImageFlags
    {
        BiggerSizeOk = 0x1
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeSize(int Width, int Height);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        internal IntPtr Icon;
        internal int IconIndex;
        internal uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        internal string TypeName;
    }

    private readonly record struct CacheKey(
        string ParsingName,
        int PixelSize,
        long ModifiedTicks,
        long Length);

    private sealed class CacheEntry(BitmapSource? image, long lastAccess)
    {
        internal BitmapSource? Image { get; } = image;
        internal long LastAccess { get; set; } = lastAccess;
    }
}
