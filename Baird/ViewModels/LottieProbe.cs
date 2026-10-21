using Avalonia.Platform;
using SkiaSharp.Skottie;
using System;
using System.IO;

namespace Baird.ViewModels
{
    /// <summary>
    /// Result of attempting to load a Lottie asset and parse it with Skottie.
    /// Exists so load failures are visible in the UI instead of silently rendering nothing.
    /// </summary>
    public sealed record LottieProbeResult(bool Loaded, string Message, double Width, double Height, double DurationSeconds, double Fps)
    {
        public string Summary => Loaded
            ? $"Loaded OK - {Width:0}x{Height:0}, {DurationSeconds:0.00}s @ {Fps:0.#}fps"
            : $"FAILED - {Message}";
    }

    public static class LottieProbe
    {
        public static LottieProbeResult Probe(string avaresPath)
        {
            try
            {
                var uri = new Uri(avaresPath);
                if (!AssetLoader.Exists(uri))
                {
                    return new LottieProbeResult(false, $"asset not embedded: {avaresPath}", 0, 0, 0, 0);
                }

                using var stream = AssetLoader.Open(uri);
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();

                if (!Animation.TryParse(json, out var animation) || animation == null)
                {
                    return new LottieProbeResult(false, "Skottie could not parse JSON", 0, 0, 0, 0);
                }

                using (animation)
                {
                    return new LottieProbeResult(true, "ok", animation.Size.Width, animation.Size.Height, animation.Duration.TotalSeconds, animation.Fps);
                }
            }
            catch (Exception ex)
            {
                return new LottieProbeResult(false, ex.Message, 0, 0, 0, 0);
            }
        }
    }
}
