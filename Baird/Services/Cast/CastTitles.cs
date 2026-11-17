using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Baird.Services.Cast
{
    /// <summary>What to call a shared link on screen. YouTube says through oEmbed (public, no key); the rest are named by service.</summary>
    public static class CastTitles
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

        public static async Task<string> LookupAsync(CastLink link, CancellationToken ct = default)
        {
            if (link.Service == "YouTube" && link.PlayUrl.Contains("watch?v=", StringComparison.Ordinal))
            {
                try
                {
                    var json = await Http.GetStringAsync($"https://www.youtube.com/oembed?format=json&url={Uri.EscapeDataString(link.PlayUrl)}", ct).ConfigureAwait(false);
                    if (ParseOEmbedTitle(json) is { } title) return title;
                }
                catch (Exception ex) { Console.WriteLine($"[Cast] YouTube title lookup failed: {ex.Message}"); }
            }
            return Fallback(link);
        }

        internal static string Fallback(CastLink link) => link.Service switch
        {
            "YouTube" => "YouTube video",
            "Netflix" or "Disney+" or "ITVX" or "Channel 4" or "BBC iPlayer" => link.Service,
            var host => $"Video from {host}",
        };

        internal static string? ParseOEmbedTitle(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty("title", out var t) && t.GetString() is { Length: > 0 } s ? s : null;
            }
            catch (JsonException) { return null; }
        }
    }
}
