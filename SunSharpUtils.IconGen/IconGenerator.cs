using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using SunSharpUtils.Ext.Math;

using Wacton.Unicolour;

namespace SunSharpUtils.IconGen;

/// <summary>
/// </summary>
public static class IconGenerator
{

    /// <summary>
    /// Taskbar icon is 24x24
    /// Top left window corner is 16x16
    /// </summary>
    public const Int32 DefaultSizePixels = 24;

}

/// <summary>
/// </summary>
public abstract class IconGenerator<TConfig>(Int32 w = IconGenerator.DefaultSizePixels)
{
    /// <summary>
    /// </summary>
    protected Int32 W { get; } = w;

    /// <summary>
    /// </summary>
    /// <param name="config"></param>
    /// <param name="w_override"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public WriteableBitmap Render(TConfig config, Int32? w_override = null)
    {
        var w = (w_override ?? this.W).ClampBottom(2);
        var bmp = new WriteableBitmap(w, w, 96, 96, PixelFormats.Bgra32, palette: null);

        if (bmp.BackBufferStride != w*4)
            throw new InvalidOperationException();
        var pixels = new Int32[w * w];

        Parallel.For(0, pixels.Length, i =>
        {
            var ix = i % w;
            var iy = i / w;

            var x = ix / (Double)(w - 1) - 0.5;
            var y = iy / (Double)(w - 1) - 0.5;

            var (alpha, color) = this.GeneratePixel(config, new Vector2D(x, y));

            var rgb = color.Rgb.Byte255;
            pixels[i] =
                (Convert.ToInt32(alpha*255).Clamp(0, 255) << 24) |
                (rgb.ConstrainedR << 16) |
                (rgb.ConstrainedG << 8) |
                (rgb.ConstrainedB << 0);
        });

        bmp.Lock();
        try
        {
            Marshal.Copy(pixels, 0, bmp.BackBuffer, pixels.Length);
            bmp.AddDirtyRect(new Int32Rect(0, 0, w, w));
        }
        finally
        {
            bmp.Unlock();
        }

        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// norm_pos is in -0.5 .. +0.5 range
    /// </summary>
    /// <param name="config"></param>
    /// <param name="norm_pos"></param>
    /// <returns></returns>
    protected abstract (Double alpha, Unicolour color) GeneratePixel(TConfig config, Vector2D norm_pos);

    /// <summary>
    /// </summary>
    protected readonly struct Vector2D(Double x, Double y)
    {
        /// <summary>
        /// </summary>
        public readonly Double X = x;
        /// <summary>
        /// </summary>
        public readonly Double Y = y;

        /// <summary>
        /// </summary>
        public static Vector2D operator *(Vector2D v, Double k) => new(v.X * k, v.Y * k);

        /// <summary>
        /// X => Right
        /// Y => Down
        /// </summary>
        /// <param name="angle_rad"></param>
        /// <returns></returns>
        public Vector2D RotateClockwise(Double angle_rad)
        {
            var rx = this.X * +Math.Cos(angle_rad) + this.Y * -Math.Sin(angle_rad);
            var ry = this.X * +Math.Sin(angle_rad) + this.Y * +Math.Cos(angle_rad);
            return new(rx, ry);
        }

        /// <summary>
        /// </summary>
        public Double Length => Math.Sqrt(this.X * this.X + this.Y * this.Y);
    }

    /// <summary>
    /// Maps: c-r .. c+r => 0 .. 1
    /// </summary>
    protected static Double MapBoundary(Double c, Double r, Double x, Boolean reverse)
    {
        if (r < 0)
            throw new ArgumentException("r must be non-negative", nameof(r));
        if (r == 0)
        {
            var cmp = x.CompareTo(c);
            if (reverse)
                cmp *= -1;
            return cmp < 0 ? 0 : cmp > 0 ? 1 : 0.5;
        }
        if (reverse)
            r *= -1;
        var r2 = 2 * r;
        return (0.5 - c / r2 + x / r2).Clamp(0, 1);
    }

}
