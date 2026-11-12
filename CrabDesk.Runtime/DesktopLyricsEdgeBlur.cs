using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace CrabDesk.Runtime;

/// <summary>Applies a spatial Gaussian blur to the top and bottom of a completed lyric frame.</summary>
internal sealed class DesktopLyricsEdgeBlur : IDisposable
{
    private const int WeightUnit = 16384;
    private const double MaximumSigma = 1.3;
    private byte[] _pixels = Array.Empty<byte>();
    private byte[] _horizontal = Array.Empty<byte>();
    private int[][] _kernels = Array.Empty<int[]>();
    private int[][] _horizontalOffsets = Array.Empty<int[]>();
    private int[][] _verticalOffsets = Array.Empty<int[]>();
    private (int Width, int Height, double Scale)? _layout;

    internal void Apply(Bitmap bitmap, double scale)
    {
        EnsureLayout(bitmap.Width, bitmap.Height, scale);
        var stride = bitmap.Width * 4;
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        try
        {
            Marshal.Copy(data.Scan0, _pixels, 0, _pixels.Length);
            for (var y = 0; y < bitmap.Height; y++)
            {
                var kernel = _kernels[y];
                var radius = kernel.Length / 2;
                var start = y * stride;
                if (radius == 0)
                {
                    Array.Copy(_pixels, start, _horizontal, start, stride);
                    continue;
                }
                var x = 0;
                if (Avx2.IsSupported)
                {
                    for (; x < Math.Min(radius, bitmap.Width); x++)
                        ConvolveHorizontalPixel(bitmap.Width, kernel, start, x);
                    for (; x + 4 <= bitmap.Width - radius; x += 4)
                        ConvolveBlock(_pixels, _horizontal, start + x * 4, kernel, _horizontalOffsets[y]);
                }
                for (; x < bitmap.Width; x++)
                {
                    ConvolveHorizontalPixel(bitmap.Width, kernel, start, x);
                }
            }
            for (var y = 0; y < bitmap.Height; y++)
            {
                var kernel = _kernels[y];
                var radius = kernel.Length / 2;
                if (radius == 0) continue;
                var start = y * stride;
                var x = 0;
                if (Avx2.IsSupported)
                    for (; x + 16 <= stride; x += 16)
                        ConvolveBlock(_horizontal, _pixels, start + x, kernel, _verticalOffsets[y]);
                for (; x < stride; x += 4)
                {
                    var blue = 0; var green = 0; var red = 0; var alpha = 0;
                    for (var offset = -radius; offset <= radius; offset++)
                    {
                        var source = Math.Clamp(y + offset, 0, bitmap.Height - 1) * stride + x;
                        var weight = kernel[offset + radius];
                        blue += _horizontal[source] * weight;
                        green += _horizontal[source + 1] * weight;
                        red += _horizontal[source + 2] * weight;
                        alpha += _horizontal[source + 3] * weight;
                    }
                    _pixels[start + x] = Rounded(blue);
                    _pixels[start + x + 1] = Rounded(green);
                    _pixels[start + x + 2] = Rounded(red);
                    _pixels[start + x + 3] = Rounded(alpha);
                }
            }
            Marshal.Copy(_pixels, 0, data.Scan0, _pixels.Length);
        }
        finally { bitmap.UnlockBits(data); }
    }

    private void ConvolveHorizontalPixel(int width, int[] kernel, int start, int x)
    {
        var radius = kernel.Length / 2;
        var blue = 0; var green = 0; var red = 0; var alpha = 0;
        for (var offset = -radius; offset <= radius; offset++)
        {
            var source = start + Math.Clamp(x + offset, 0, width - 1) * 4;
            var weight = kernel[offset + radius];
            blue += _pixels[source] * weight;
            green += _pixels[source + 1] * weight;
            red += _pixels[source + 2] * weight;
            alpha += _pixels[source + 3] * weight;
        }
        var target = start + x * 4;
        _horizontal[target] = Rounded(blue);
        _horizontal[target + 1] = Rounded(green);
        _horizontal[target + 2] = Rounded(red);
        _horizontal[target + 3] = Rounded(alpha);
    }

    private static void ConvolveBlock(byte[] source, byte[] target, int start, int[] kernel, int[] offsets)
    {
        var low = Vector256<int>.Zero;
        var high = Vector256<int>.Zero;
        for (var index = 0; index < kernel.Length; index++)
        {
            // Callers only pass complete four-pixel blocks within a source
            // row; the scalar path handles edges and short rows.
            var pixels = Vector128.LoadUnsafe(ref source[0], (nuint)(start + offsets[index]));
            var weight = Vector256.Create(kernel[index]);
            low = Avx2.Add(low, Avx2.MultiplyLow(Avx2.ConvertToVector256Int32(pixels), weight));
            high = Avx2.Add(high, Avx2.MultiplyLow(Avx2.ConvertToVector256Int32(
                Sse2.ShiftRightLogical128BitLane(pixels, 8)), weight));
        }
        var rounding = Vector256.Create(WeightUnit / 2);
        low = Avx2.ShiftRightLogical(Avx2.Add(low, rounding), 14);
        high = Avx2.ShiftRightLogical(Avx2.Add(high, rounding), 14);
        var packed = Avx2.Permute4x64(Avx2.PackUnsignedSaturate(low, high).AsInt64(), 0xd8).AsInt16();
        Sse2.PackUnsignedSaturate(packed.GetLower(), packed.GetUpper()).StoreUnsafe(ref target[0], (nuint)start);
    }

    private void EnsureLayout(int width, int height, double scale)
    {
        var layout = (width, height, scale);
        if (_layout == layout) return;
        _layout = layout;
        _pixels = new byte[width * height * 4];
        _horizontal = new byte[_pixels.Length];
        _kernels = new int[height][];
        _horizontalOffsets = new int[height][];
        _verticalOffsets = new int[height][];
        for (var y = 0; y < (height + 1) / 2; y++)
        {
            var t = Math.Clamp(1 - (y + .5) / Math.Max(1, height * .28), 0, 1);
            var sigma = MaximumSigma * scale * t * t * (3 - 2 * t);
            var kernel = CreateKernel(sigma);
            _kernels[y] = kernel;
            _kernels[height - 1 - y] = kernel;
        }
        for (var y = 0; y < height; y++)
        {
            var radius = _kernels[y].Length / 2;
            _horizontalOffsets[y] = new int[_kernels[y].Length];
            _verticalOffsets[y] = new int[_kernels[y].Length];
            for (var offset = -radius; offset <= radius; offset++)
            {
                _horizontalOffsets[y][offset + radius] = offset * 4;
                _verticalOffsets[y][offset + radius] = (Math.Clamp(y + offset, 0, height - 1) - y) * width * 4;
            }
        }
    }

    private static int[] CreateKernel(double sigma)
    {
        if (sigma < .15) return [WeightUnit];
        var radius = (int)Math.Ceiling(sigma * 3);
        var kernel = new int[radius * 2 + 1];
        var total = 0d;
        for (var offset = -radius; offset <= radius; offset++)
            total += Math.Exp(-offset * offset / (2 * sigma * sigma));
        var roundedTotal = 0;
        for (var offset = -radius; offset <= radius; offset++)
        {
            var weight = (int)Math.Round(WeightUnit * Math.Exp(-offset * offset / (2 * sigma * sigma)) / total);
            kernel[offset + radius] = weight;
            roundedTotal += weight;
        }
        kernel[radius] += WeightUnit - roundedTotal;
        return kernel;
    }

    private static byte Rounded(int value) => (byte)((value + WeightUnit / 2) / WeightUnit);

    public void Dispose()
    {
        _pixels = _horizontal = Array.Empty<byte>();
        _kernels = Array.Empty<int[]>();
        _horizontalOffsets = _verticalOffsets = Array.Empty<int[]>();
        _layout = null;
    }
}
