using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

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

    public Bitmap? GetIcon(string parsingName) => GetIcon(parsingName, 48);

    public Bitmap? GetIcon(string parsingName, int pixelSize)
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
            foreach (var entry in _cache.Values)
            {
                entry.Image?.Dispose();
            }
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
            _cache[oldest].Image?.Dispose();
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

    private static Bitmap? LoadImage(string parsingName, int pixelSize) =>
        LoadShellItemImage(parsingName, pixelSize) ?? LoadLegacyIcon(parsingName);

    private static Bitmap? LoadShellItemImage(string parsingName, int pixelSize)
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
                ShellItemImageFlags.BiggerSizeOk |
                ShellItemImageFlags.IconOnly |
                ShellItemImageFlags.ScaleUp,
                out bitmapHandle);
            if (result != 0 || bitmapHandle == IntPtr.Zero)
            {
                return null;
            }
            return CreateAlphaBitmap(bitmapHandle, pixelSize);
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

    private static Bitmap? LoadLegacyIcon(string parsingName)
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
            using var icon = (Icon)Icon.FromHandle(iconHandle).Clone();
            return icon.ToBitmap();
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

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int size, out NativeBitmap bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr deviceContext,
        IntPtr bitmap,
        uint startScan,
        uint scanLines,
        IntPtr bits,
        ref BitmapInfo bitmapInfo,
        uint usage);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

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
        BiggerSizeOk = 0x1,
        IconOnly = 0x4,
        ScaleUp = 0x100
    }

    private static Bitmap? CreateAlphaBitmap(IntPtr bitmapHandle, int pixelSize)
    {
        if (GetObject(bitmapHandle, Marshal.SizeOf<NativeBitmap>(), out var native) == 0)
        {
            return null;
        }
        var width = Math.Abs(native.Width);
        var height = Math.Abs(native.Height);
        if (width == 0 || height == 0)
        {
            return null;
        }

        var source = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = source.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = 0,
                SizeImage = (uint)(Math.Abs(data.Stride) * height)
            }
        };
        var deviceContext = GetDC(IntPtr.Zero);
        var copied = 0;
        try
        {
            copied = GetDIBits(
                deviceContext,
                bitmapHandle,
                0,
                (uint)height,
                data.Scan0,
                ref info,
                0);
        }
        finally
        {
            if (deviceContext != IntPtr.Zero) ReleaseDC(IntPtr.Zero, deviceContext);
            source.UnlockBits(data);
        }
        if (copied == 0 || !HasAlpha(source))
        {
            source.Dispose();
            return null;
        }
        if (width == pixelSize && height == pixelSize)
        {
            return source;
        }

        var scaled = new Bitmap(pixelSize, pixelSize, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.Clear(Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source, new Rectangle(0, 0, pixelSize, pixelSize));
        }
        source.Dispose();
        return scaled;
    }

    private static bool HasAlpha(Bitmap bitmap)
    {
        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[Math.Abs(data.Stride) * bitmap.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            for (var index = 3; index < bytes.Length; index += 4)
            {
                if (bytes[index] != 0) return true;
            }
            return false;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeSize(int Width, int Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        internal int Type;
        internal int Width;
        internal int Height;
        internal int WidthBytes;
        internal ushort Planes;
        internal ushort BitsPixel;
        internal IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort BitCount;
        internal uint Compression;
        internal uint SizeImage;
        internal int XPelsPerMeter;
        internal int YPelsPerMeter;
        internal uint ClrUsed;
        internal uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        internal BitmapInfoHeader Header;
        internal uint Colors;
    }

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

    private sealed class CacheEntry(Bitmap? image, long lastAccess)
    {
        internal Bitmap? Image { get; } = image;
        internal long LastAccess { get; set; } = lastAccess;
    }
}
