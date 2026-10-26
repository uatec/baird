using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Baird.NewUi.Controls
{
    /// <summary>
    /// A channel's brand colour, from its logo: the most common strong hue, ignoring transparency, white, black and grey.
    /// Used for the last step of the artwork fallback (guide picture → TMDB → a card in the channel's own colours).
    /// </summary>
    public static class BrandColour
    {
        private const int Size = 24, Bins = 36;

        public static Color? Of(Bitmap source)
        {
            try
            {
                var w = source.PixelSize.Width; var h = source.PixelSize.Height;
                if (w <= 0 || h <= 0) return null;
                var stride = w * 4;
                var bytes = new byte[stride * h];
                var buf = Marshal.AllocHGlobal(bytes.Length);
                try
                {
                    source.CopyPixels(new PixelRect(0, 0, w, h), buf, bytes.Length, stride);
                    Marshal.Copy(buf, bytes, 0, bytes.Length);
                }
                finally { Marshal.FreeHGlobal(buf); }
                var rgba = source.Format == PixelFormat.Rgba8888;
                int stepX = Math.Max(1, w / Size), stepY = Math.Max(1, h / Size);

                // Bgra8888: weight each pixel's hue by its saturation, among visible, colourful pixels.
                var weight = new double[Bins];
                var value = new double[Bins];
                for (var y = 0; y < h; y += stepY)
                    for (var x = 0; x < w; x += stepX)
                    {
                        var i = y * stride + x * 4;
                        if (bytes[i + 3] < 128) continue;
                        var c = rgba ? Color.FromRgb(bytes[i], bytes[i + 1], bytes[i + 2]) : Color.FromRgb(bytes[i + 2], bytes[i + 1], bytes[i]);
                        var hsv = c.ToHsv();
                        if (hsv.S < 0.35 || hsv.V < 0.2) continue;
                        var bin = (int)(hsv.H / 360 * Bins) % Bins;
                        weight[bin] += hsv.S;
                        value[bin] += hsv.V * hsv.S;
                    }
                var best = -1;
                for (var b = 0; b < Bins; b++) if (weight[b] > 0 && (best < 0 || weight[b] > weight[best])) best = b;
                if (best < 0 || weight[best] < 4) return null; // nothing colourful enough to call a brand colour
                var hue = (best + 0.5) * 360.0 / Bins;
                return HsvColor.FromAhsv(1, hue, 0.75, Math.Clamp(value[best] / weight[best], 0.4, 0.8)).ToRgb();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BrandColour] {ex.Message}");
                return null;
            }
        }
    }
}
