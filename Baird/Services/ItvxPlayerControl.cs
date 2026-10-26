using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Baird.Services
{
    /// <summary>What an ITVX page shows: the sign-in state, the buttons Baird presses and the video.</summary>
    /// <param name="SignInPage">The page is one of ITVX's sign-in steps (<c>/watch/user/...</c>).</param>
    /// <param name="SignedIn">The page shows the account menu, or ITVX's session cookie is set.</param>
    /// <param name="PlayButton">The title page's own Play button (WATCH NOW / PLAY): pages do not start by themselves.</param>
    /// <param name="ParentalPrompt">The "Parental Controls are off" dialog a title with guidance shows before it plays.</param>
    /// <param name="HasVideo">The programme video (the <c>blob:</c> one) is on the page.</param>
    /// <param name="AdvertPlaying">Another video (an advert, from ITVX's advert CDN) plays while the programme waits paused at 0.</param>
    public sealed record ItvxPageState(
        string Url,
        bool SignInPage,
        bool SignedIn,
        bool EmailField,
        bool PasswordField,
        bool PlayButton,
        bool ParentalPrompt,
        bool HasVideo,
        double CurrentTime,
        double Duration,
        bool Paused,
        bool Ended,
        bool AdvertPlaying,
        double AdvertTime,
        string Text);

    /// <summary>
    /// Reads and controls ITVX's web player over CDP. Measured on the Pi, 2026-10-26 (see docs/itvx-investigation.md §8):
    /// <list type="bullet">
    /// <item>A title page does not play by itself: <c>button.videoPlayer__play-btn</c> (WATCH NOW / PLAY) starts it. A title with
    /// guidance first shows "Parental Controls are off" (in <c>div.dialogs-container-wrapper</c>) with its own WATCH NOW.</item>
    /// <item>Signed out, Play goes to <c>/watch/user/signin</c>. Signed in, the header has a button "Open account menu" and the page an
    /// <c>Itv.Session</c> cookie.</item>
    /// <item>The programme is a <c>blob:</c> &lt;video&gt; whose <c>duration</c> is the programme's real length and <c>currentTime</c> the
    /// real position. Adverts play in OTHER &lt;video&gt; elements while the programme waits paused at 0. A native seek, pause and play
    /// work on it.</item>
    /// <item>The cookie banner's REJECT accepts a script click.</item>
    /// </list>
    /// </summary>
    public static class ItvxPlayerControl
    {
        /// <summary>JS that finds the programme video (<c>v</c>, the <c>blob:</c> one) and a playing advert (<c>ad</c>).</summary>
        public const string FindVideos = @"const vids = [...document.querySelectorAll('video')].filter(x => x.currentSrc);
            const v = vids.find(x => x.currentSrc.startsWith('blob:')) || null;
            const ad = vids.find(x => x !== v && !x.paused && !x.ended) || null;";

        public static string StateScript => @"(function() {
            try { const c = [...document.querySelectorAll('button')].find(b => /^reject$/i.test(b.innerText.trim())); if (c) c.click(); } catch (e) { }
            " + FindVideos + @"
            const dialog = [...document.querySelectorAll('.dialogs-container-wrapper, [role=dialog]')].find(d => /parental controls/i.test(d.innerText));
            const text = (document.body ? document.body.innerText : '').replace(/\s+/g, ' ');
            return {
                url: location.href,
                signInPage: location.pathname.startsWith('/watch/user/'),
                signedIn: !!document.querySelector('[aria-label=""Open account menu""]') || /(^|;\s*)Itv\.Session=/.test(document.cookie),
                emailField: !!document.querySelector('input[type=email]'),
                passwordField: !!document.querySelector('input[type=password]'),
                playButton: !!document.querySelector('button.videoPlayer__play-btn'),
                parentalPrompt: !!dialog,
                hasVideo: !!v,
                currentTime: v ? v.currentTime : 0,
                duration: v && isFinite(v.duration) ? v.duration : 0,
                paused: v ? v.paused : true,
                ended: v ? v.ended : false,
                advertPlaying: !!ad,
                advertTime: ad ? ad.currentTime : 0,
                text: text.slice(0, 240)
            };
        })()";

        public static ItvxPageState? ParseState(JsonElement? result)
        {
            if (result is not { ValueKind: JsonValueKind.Object } r) return null;
            return new ItvxPageState(
                Str(r, "url") ?? "", Bool(r, "signInPage"), Bool(r, "signedIn"), Bool(r, "emailField"), Bool(r, "passwordField"),
                Bool(r, "playButton"), Bool(r, "parentalPrompt"), Bool(r, "hasVideo"), Num(r, "currentTime"), Num(r, "duration"),
                !r.TryGetProperty("paused", out var p) || p.ValueKind != JsonValueKind.False,
                Bool(r, "ended"), Bool(r, "advertPlaying"), Num(r, "advertTime"), Str(r, "text") ?? "");
        }

        public static async Task<ItvxPageState?> ReadStateAsync(ICdpPageDriver page, CancellationToken ct = default)
        {
            try { return ParseState(await page.EvaluateAsync(StateScript, ct)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return null; } // the page is navigating (sign-in steps): no answer this time
        }

        /// <summary>JS that presses the title page's Play button. Returns "clicked" or null.</summary>
        public const string PressPlayScript = "(function() { const b = document.querySelector('button.videoPlayer__play-btn'); if (!b) return null; b.click(); return 'clicked'; })()";

        /// <summary>JS that answers "Parental Controls are off" with its WATCH NOW. Returns "clicked" or null.</summary>
        public const string AcceptParentalScript = @"(function() {
            const d = [...document.querySelectorAll('.dialogs-container-wrapper, [role=dialog]')].find(x => /parental controls/i.test(x.innerText));
            const b = d && [...d.querySelectorAll('button')].find(x => /^watch now$/i.test(x.innerText.trim()));
            if (!b) return null; b.click(); return 'clicked';
        })()";

        /// <summary>
        /// JS that presses the enabled button whose text is one of <paramref name="labels"/> (case-insensitive), e.g. CONTINUE, SIGN IN.
        /// Returns "clicked" or null.
        /// </summary>
        public static string PressButtonScript(params string[] labels) => @"(function(labels) {
            const b = [...document.querySelectorAll('button')].find(x => !x.disabled && labels.includes(x.innerText.trim().toLowerCase()));
            if (!b) return null; b.click(); return 'clicked';
        })([" + string.Join(", ", Array.ConvertAll(labels, l => JsString(l.ToLowerInvariant()))) + "])";

        /// <summary>JS that follows "Use password instead" on the emailed-code step. Returns "clicked" or null.</summary>
        public const string UsePasswordScript = @"(function() {
            const a = [...document.querySelectorAll('a, button')].find(x => /use password instead/i.test(x.innerText));
            if (!a) return null; a.click(); return 'clicked';
        })()";

        /// <summary>JS for pause and play: pause stops every playing video; play resumes the advert if one is under way, else the programme.</summary>
        public static string ControlScript(NetflixPlayerAction action) => $@"(function(action) {{
            {FindVideos}
            if (!v) return null;
            if (action === 'Pause') {{ vids.forEach(x => {{ if (!x.paused) x.pause(); }}); return 'video'; }}
            if (action === 'Play') {{ const live = vids.find(x => x !== v && x.currentTime > 0 && !x.ended) || v; live.play(); return 'video'; }}
            return null;
        }})('{action}')";

        /// <summary>JS that seeks the programme video by <paramref name="seconds"/> (clamped to the video). Returns "video" or null.</summary>
        public static string SeekScript(double seconds) => @"(function(by) {
            " + FindVideos + @"
            if (!v || ad) return null;
            const end = isFinite(v.duration) && v.duration > 0 ? v.duration - 1 : v.currentTime + Math.max(by, 0);
            v.currentTime = Math.max(0, Math.min(end, v.currentTime + by));
            return 'video';
        })(" + seconds.ToString(CultureInfo.InvariantCulture) + ")";

        /// <summary>Plays, pauses or seeks. Returns the path that accepted it ("video"), or null when the page has no player.</summary>
        public static async Task<string?> ControlAsync(ICdpPageDriver page, NetflixPlayerAction action, double seconds, CancellationToken ct = default)
        {
            var script = action == NetflixPlayerAction.SeekBy ? SeekScript(seconds) : ControlScript(action);
            var result = await page.EvaluateAsync(script, ct);
            return result is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
        }

        /// <summary>
        /// JS that fetches a programme page from inside an itv.com page (www.itv.com answers only real browsers) and returns its data,
        /// <c>{programme, seriesList}</c>, as a JSON string, or <c>{error}</c>. Only the fields Baird reads are kept, under ITVX's names.
        /// </summary>
        public static string ProgrammeFetchScript(string path) => @"(async function(path) {
            try {
                const r = await fetch(path, { credentials: 'include' });
                if (!r.ok) return { error: 'HTTP ' + r.status };
                const doc = new DOMParser().parseFromString(await r.text(), 'text/html');
                const nd = doc.getElementById('__NEXT_DATA__');
                if (!nd) return { error: 'the page has no data (__NEXT_DATA__)' };
                const p = JSON.parse(nd.textContent).props.pageProps;
                if (!p || !p.programme || !p.seriesList) return { error: 'the page data has no programme' };
                const keep = ['ccid', 'episode', 'episodeTitle', 'description', 'notFormattedDuration', 'image', 'premium', 'tier', 'encodedEpisodeId'];
                return JSON.stringify({
                    programme: { ccid: p.programme.ccid, title: p.programme.title, titleSlug: p.programme.titleSlug, tier: p.programme.tier },
                    seriesList: p.seriesList.map(s => ({ seriesNumber: s.seriesNumber, seriesLabel: s.seriesLabel,
                        titles: (s.titles || []).map(t => Object.fromEntries(keep.map(k => [k, t[k]]))) }))
                });
            } catch (e) { return { error: String(e) }; }
        })(" + JsString(path) + ")";

        /// <summary>A JS string literal for <paramref name="s"/> (quotes, backslashes and markup characters escaped).</summary>
        public static string JsString(string s) => "\"" + JsonEncodedText.Encode(s).ToString() + "\"";

        private static string? Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static bool Bool(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        private static double Num(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    }
}
