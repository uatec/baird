using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Baird.Services
{
    /// <summary>
    /// Reads and controls Disney+'s player from Baird, through its &lt;video&gt; element (measured on the Pi): the page has TWO
    /// &lt;video&gt; elements and the first stays empty, so the one with a source is used. Setting <c>currentTime</c> seeks (the
    /// player buffers and carries on), and <c>pause()</c> / <c>play()</c> work: no player API is needed, unlike Netflix.
    /// <c>video.duration</c> is not the length of the title (it is the buffered end, or Infinity), so the length comes from
    /// Disney's API and the poll does not report a duration.
    /// </summary>
    public static class DisneyPlayerControl
    {
        /// <summary>JS that finds the playing video: the one with a source, else the first.</summary>
        public const string FindVideo =
            "const v = [...document.querySelectorAll('video')].find(x => x.readyState > 0 && x.currentSrc) || document.querySelector('video');";

        public static string PollScript => @"(function() {
            " + FindVideo + @"
            if (!v) return { found: false, currentTime: 0, readyState: 0, paused: true, ended: false, url: location.href, title: document.title };
            return { found: true, currentTime: v.currentTime, readyState: v.readyState, paused: v.paused, ended: v.ended, url: location.href, title: document.title };
        })()";

        public static string ControlScript(NetflixPlayerAction action, double seconds, double runtimeSeconds)
        {
            var secs = seconds.ToString(CultureInfo.InvariantCulture);
            var max = runtimeSeconds > 0 ? runtimeSeconds.ToString(CultureInfo.InvariantCulture) : "Infinity";
            return $@"(function(action, seconds, max) {{
                {FindVideo}
                if (!v) return null;
                if (action === 'SeekBy') {{ v.currentTime = Math.max(0, Math.min(v.currentTime + seconds, max)); return 'video'; }}
                if (action === 'Play') {{ v.play(); return 'video'; }}
                if (action === 'Pause') {{ v.pause(); return 'video'; }}
                return null;
            }})('{action}', {secs}, {max})";
        }

        /// <summary>The playing video's state. <see cref="VideoPlaybackStatus.Duration"/> is 0: the page does not know the length.</summary>
        public static async Task<VideoPlaybackStatus?> PollAsync(ICdpPageDriver page, CancellationToken ct = default)
        {
            var result = await page.EvaluateAsync(PollScript, ct);
            if (result is not { ValueKind: JsonValueKind.Object } r) return null;
            return new VideoPlaybackStatus(
                Found: r.TryGetProperty("found", out var f) && f.ValueKind == JsonValueKind.True,
                CurrentTime: r.TryGetProperty("currentTime", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0,
                ReadyState: r.TryGetProperty("readyState", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0,
                Paused: !r.TryGetProperty("paused", out var p) || p.ValueKind != JsonValueKind.False,
                Duration: 0,
                Url: r.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                Title: r.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                Ended: r.TryGetProperty("ended", out var e) && e.ValueKind == JsonValueKind.True);
        }

        /// <summary>Plays, pauses or seeks. Returns "video" when it was accepted, or null when the page has no player.</summary>
        public static async Task<string?> ControlAsync(ICdpPageDriver page, NetflixPlayerAction action, double seconds, double runtimeSeconds, CancellationToken ct = default)
        {
            var result = await page.EvaluateAsync(ControlScript(action, seconds, runtimeSeconds), ct);
            return result is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
        }
    }
}
