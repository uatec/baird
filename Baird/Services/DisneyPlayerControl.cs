using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Baird.Services
{
    /// <summary>
    /// Reads and controls Disney+'s player from Baird. Everything here was measured on the Pi (2026-10-25):
    /// <list type="bullet">
    /// <item><b>The element's time is not the title's time.</b> Disney+ restarts the video session after every seek and at resume,
    /// so <c>video.currentTime</c> restarts near 23 s and sits at an arbitrary offset from the position in the title (offsets of
    /// 137 s, 301 s, 317 s were seen). The real position is on the controls' timeline: the length is the slider's
    /// <c>aria-valuemax</c> (exact) and the live "time remaining" text counts down in seconds; position = length - remaining.
    /// The controls are only in the page while they are awake, so a poll first moves the pointer.</item>
    /// <item><b>A native seek can stall for good</b> (<c>video.currentTime</c> to just before the buffered range; the buffer is only
    /// about 10 to 20 s around the playhead). Disney's own "Skip back/ahead 10 seconds" buttons seek exactly in title time, so
    /// seeking clicks them. <c>video.pause()</c> and <c>play()</c> are safe.</item>
    /// <item>The page has TWO &lt;video&gt; elements and the first stays empty: the one with a source is used.</item>
    /// <item><b>"SKIP INTRO"</b> (and the recap, credits and opening variants) is a &lt;button&gt; in <c>skip-overlay</c>, outside the
    /// controls layer. Clicking it jumped from 12 s to 72 s.</item>
    /// <item><b>The end card</b> (<c>end-card-overlay</c>: "UP NEXT IN 5", the next episode, a "Close" button) appears about 35 s
    /// before the end, and Disney+ plays the next episode when its countdown ends.</item>
    /// </list>
    /// </summary>
    public static class DisneyPlayerControl
    {
        /// <summary>JS helpers: visit every element including those in shadow roots (where Disney+'s player UI lives).</summary>
        public const string Helpers = @"const walk = (fn) => { const visit = (root) => { for (const e of root.querySelectorAll('*')) { fn(e); if (e.shadowRoot) visit(e.shadowRoot); } }; visit(document); };
            const clock = (text) => { const m = /^(?:(\d+):)?(\d+):(\d+)$/.exec((text || '').trim()); return m ? (+(m[1] || 0)) * 3600 + (+m[2]) * 60 + (+m[3]) : null; };
            const hostOf = (e) => { const h = e.getRootNode && e.getRootNode().host; return h ? h.tagName : ''; };
            const labelOf = (e) => ((e.getAttribute && e.getAttribute('aria-label')) || e.textContent || '').replace(/\s+/g, ' ').trim();
            const shown = (e) => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0; };";

        /// <summary>JS that finds the playing video: the one with a source, else the first.</summary>
        public const string FindVideo =
            "const v = [...document.querySelectorAll('video')].find(x => x.readyState > 0 && x.currentSrc) || document.querySelector('video');";

        /// <summary>The prompt shown for Disney+'s end card (the next episode is about to start).</summary>
        public const string NextEpisodeLabel = "Next episode";

        public static string PollScript => @"(function() {
            " + Helpers + @"
            " + FindVideo + @"
            const SKIP = /^skip (intro|recap|credits|opening|previously)$/i;
            let slider = null, remaining = null, skip = null, endCard = false;
            walk((e) => {
                if (!slider && e.getAttribute && e.getAttribute('role') === 'slider') slider = e;
                if (remaining === null && typeof e.className === 'string' && e.className.indexOf('time-remaining') >= 0) remaining = clock(e.textContent);
                if (e.tagName === 'BUTTON' && shown(e)) {
                    const label = labelOf(e);
                    if (!skip && SKIP.test(label)) skip = label;
                    if (hostOf(e) === 'END-CARD-OVERLAY' && /^close$/i.test(label)) endCard = true;
                }
            });
            const length = slider ? +slider.getAttribute('aria-valuemax') : null;
            const titleSeconds = (length > 0 && remaining !== null) ? Math.max(0, length - remaining) : null;
            const prompt = skip || (endCard ? '" + NextEpisodeLabel + @"' : null);
            const base = { prompt: prompt, titleSeconds: titleSeconds, titleLength: length > 0 ? length : null, url: location.href, title: document.title };
            if (!v) return Object.assign(base, { found: false, currentTime: 0, readyState: 0, paused: true, ended: false });
            return Object.assign(base, { found: true, currentTime: v.currentTime, readyState: v.readyState, paused: v.paused, ended: v.ended });
        })()";

        /// <summary>JS that clicks the skip prompt ("Skip intro", ...). Returns "clicked", or null when there is none.</summary>
        public static string ActivateSkipScript => @"(function() {
            " + Helpers + @"
            const SKIP = /^skip (intro|recap|credits|opening|previously)$/i;
            let found = null;
            walk((e) => { if (!found && e.tagName === 'BUTTON' && shown(e) && SKIP.test(labelOf(e))) found = e; });
            if (!found) return null;
            found.click();
            return 'clicked';
        })()";

        /// <summary>JS that closes Disney+'s end card, so that Disney+ does not start the next episode itself. Returns "clicked" or null.</summary>
        public static string DismissEndCardScript => @"(function() {
            " + Helpers + @"
            let found = null;
            walk((e) => { if (!found && e.tagName === 'BUTTON' && shown(e) && hostOf(e) === 'END-CARD-OVERLAY' && /^close$/i.test(labelOf(e))) found = e; });
            if (!found) return null;
            found.click();
            return 'clicked';
        })()";

        /// <summary>JS for pause and play on the &lt;video&gt;.</summary>
        public static string ControlScript(NetflixPlayerAction action)
        {
            return $@"(function(action) {{
                {FindVideo}
                if (!v) return null;
                if (action === 'Play') {{ v.play(); return 'video'; }}
                if (action === 'Pause') {{ v.pause(); return 'video'; }}
                return null;
            }})('{action}')";
        }

        /// <summary>The number of Disney+ skip clicks (10 s each) for a seek of <paramref name="seconds"/>: at least 1, at most 30.</summary>
        public static int SkipClicks(double seconds) => (int)Math.Clamp(Math.Round(Math.Abs(seconds) / 10.0, MidpointRounding.AwayFromZero), 1, 30);

        /// <summary>
        /// JS that clicks Disney+'s own skip button <paramref name="clicks"/> times, 300 ms apart (the controls must be awake: the
        /// buttons are only in the page then). Returns "buttons", or null when the button is not there.
        /// </summary>
        public static string SkipScript(bool forward, int clicks)
        {
            var label = forward ? "Skip ahead 10 seconds" : "Skip back 10 seconds";
            return @"(async function(label, clicks) {
                " + Helpers + @"
                let button = null;
                walk((e) => { if (!button && e.getAttribute && e.getAttribute('aria-label') === label) button = e; });
                if (!button) return null;
                for (let i = 0; i < clicks; i++) {
                    button.click();
                    if (i < clicks - 1) await new Promise((r) => setTimeout(r, 300));
                }
                return 'buttons';
            })('" + label + "', " + clicks.ToString(CultureInfo.InvariantCulture) + ")";
        }

        /// <summary>"SKIP INTRO" and "Skip Intro" both show as "Skip intro".</summary>
        public static string Normalize(string label)
        {
            var t = label.Trim();
            return t.Length == 0 ? t : char.ToUpperInvariant(t[0]) + t[1..].ToLowerInvariant();
        }

        // The pointer alternates between two points so that every poll is a real movement (the controls stay awake).
        private static int _wiggle;
        private static async Task WakeControlsAsync(ICdpPageDriver page, CancellationToken ct)
        {
            var x = 900 + (Interlocked.Increment(ref _wiggle) % 2) * 6;
            await page.MoveMouseAsync(x, 520, ct);
        }

        /// <summary>
        /// The player's state: the video, the position and length in the TITLE (null while the controls are not in the page), and the
        /// prompt on screen ("Skip intro", or "Next episode" for the end card).
        /// </summary>
        public static async Task<VideoPlaybackStatus?> PollAsync(ICdpPageDriver page, CancellationToken ct = default)
        {
            await WakeControlsAsync(page, ct);
            await Task.Delay(60, ct); // the controls take a moment to be in the page after the pointer moves
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
                Ended: r.TryGetProperty("ended", out var e) && e.ValueKind == JsonValueKind.True,
                Prompt: r.TryGetProperty("prompt", out var pr) && pr.ValueKind == JsonValueKind.String && pr.GetString() is { Length: > 0 } label ? Normalize(label) : null,
                TitleSeconds: r.TryGetProperty("titleSeconds", out var ts) && ts.ValueKind == JsonValueKind.Number ? ts.GetDouble() : null,
                TitleLengthSeconds: r.TryGetProperty("titleLength", out var tl) && tl.ValueKind == JsonValueKind.Number ? tl.GetDouble() : null);
        }

        /// <summary>Clicks the skip prompt on screen. True when there was one.</summary>
        public static async Task<bool> ActivateSkipAsync(ICdpPageDriver page, CancellationToken ct = default)
        {
            await WakeControlsAsync(page, ct);
            var result = await page.EvaluateAsync(ActivateSkipScript, ct);
            return result is { ValueKind: JsonValueKind.String };
        }

        /// <summary>Closes the end card. True when it was there.</summary>
        public static async Task<bool> DismissEndCardAsync(ICdpPageDriver page, CancellationToken ct = default)
        {
            var result = await page.EvaluateAsync(DismissEndCardScript, ct);
            return result is { ValueKind: JsonValueKind.String };
        }

        /// <summary>
        /// Plays, pauses, or seeks (by Disney+'s own skip buttons, in 10 s steps). Returns the path that accepted it ("video" or
        /// "buttons"), or null when the page has no player or the button is not there.
        /// </summary>
        public static async Task<string?> ControlAsync(ICdpPageDriver page, NetflixPlayerAction action, double seconds, CancellationToken ct = default)
        {
            if (action == NetflixPlayerAction.SeekBy)
            {
                await WakeControlsAsync(page, ct);
                await Task.Delay(120, ct);
                var seek = await page.EvaluateAsync(SkipScript(seconds > 0, SkipClicks(seconds)), ct);
                return seek is { ValueKind: JsonValueKind.String } sv ? sv.GetString() : null;
            }
            var result = await page.EvaluateAsync(ControlScript(action), ct);
            return result is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
        }
    }
}
