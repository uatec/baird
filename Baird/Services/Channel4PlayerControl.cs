using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Baird.Services
{
    /// <summary>What a Channel 4 page shows: the sign-in state and the video.</summary>
    /// <param name="Authenticated">The page's own <c>__PARAMS__.initialData.isUserAuthenticated</c>; null on a page without it (sign-in pages, robots.txt).</param>
    /// <param name="SignInPrompt">"Please sign in to watch" is on the page.</param>
    /// <param name="OtherVideoPlaying">A video other than the programme plays (an advert).</param>
    /// <param name="AdvertTime">That advert's own time (the programme video stays paused at 0 while adverts play).</param>
    /// <param name="PlayButton">The page shows its Play button (it does not always start the video by itself).</param>
    public sealed record Channel4PageState(
        string Url,
        bool? Authenticated,
        bool SignInPrompt,
        bool EmailField,
        bool PasswordField,
        bool HasVideo,
        double CurrentTime,
        double Duration,
        bool Paused,
        bool Ended,
        bool OtherVideoPlaying,
        string Text,
        double AdvertTime = 0,
        bool PlayButton = false)
    {
        /// <summary>The page asks for a sign-in (or shows the sign-in form).</summary>
        public bool NeedsSignIn => Authenticated == false || SignInPrompt || EmailField || PasswordField;
    }

    /// <summary>
    /// Reads and controls Channel 4's web player from Baird, over CDP. What is known (2026-10-25, signed out, on the dev PC):
    /// <list type="bullet">
    /// <item>Programme and on-demand pages carry <c>window.__PARAMS__.initialData.isUserAuthenticated</c>. Signed out, the on-demand
    /// page shows "Please sign in to watch" with Sign In / Register buttons instead of the player.</item>
    /// <item>Sign-in (<c>/sign-in-or-register</c>) refuses automated browsers: its email check (<c>POST /identity/check-email</c>,
    /// with a reCAPTCHA Enterprise token) answered 401 <c>E_RECAPTCHA_THRESHOLD</c>, and on the Pi an Akamai "Your Javascript is
    /// disabled" page. Baird does not sign in; the viewer does, once, with <c>channel4_signin.sh</c>.</item>
    /// <item>The cookie banner's "Accept &amp; Continue" (<c>.all4-cc-primary-button</c>) accepts a script click.</item>
    /// </list>
    /// Measured on the Pi, signed in (2026-10-25): the page has two &lt;video&gt; elements. The programme is a <c>blob:</c> video whose
    /// <c>duration</c> is the programme's real length (2089.9 s for a "35 mins" Countdown); the adverts play in the OTHER element
    /// (15 s and 30 s clips from Channel 4's advert CDN), while the programme waits paused at 0. The page shows "Advert: Programme starts
    /// in 01:06". NOT yet measured: whether a native seek is safe, mid-roll adverts, the end of a programme.
    /// </summary>
    public static class Channel4PlayerControl
    {
        /// <summary>A video shorter than this, while the programme is much longer, is taken to be an advert.</summary>
        public const double AdvertMaxSeconds = 240;

        /// <summary>JS that finds the programme video (<c>v</c>, the longest finite duration among videos with a source) and an advert (<c>ad</c>).</summary>
        public const string FindVideos = @"const vids = [...document.querySelectorAll('video')].filter(x => x.readyState > 0 && x.currentSrc);
            const dur = (x) => isFinite(x.duration) ? x.duration : 0;
            const v = vids.reduce((best, x) => (!best || dur(x) > dur(best)) ? x : best, null);
            const ad = vids.find(x => x !== v && !x.paused && !x.ended) || null;";

        public static string StateScript => @"(function() {
            try { const c = document.querySelector('.all4-cc-primary-button'); if (c) c.click(); } catch (e) { }
            " + FindVideos + @"
            let auth = null;
            try { const d = window.__PARAMS__ && window.__PARAMS__.initialData; if (d && typeof d.isUserAuthenticated === 'boolean') auth = d.isUserAuthenticated; } catch (e) { }
            const text = (document.body ? document.body.innerText : '').replace(/\s+/g, ' ');
            return {
                url: location.href,
                authenticated: auth,
                signInPrompt: /please sign in to watch/i.test(text),
                emailField: !!document.querySelector('#email-field, input[type=email]'),
                passwordField: !!document.querySelector('input[type=password]'),
                hasVideo: !!v,
                currentTime: v ? v.currentTime : 0,
                duration: v ? dur(v) : 0,
                paused: v ? v.paused : true,
                ended: v ? v.ended : false,
                otherVideoPlaying: !!ad,
                advertTime: ad ? ad.currentTime : 0,
                playButton: !!document.querySelector('button.text-button.play'),
                text: text.slice(0, 240)
            };
        })()";

        /// <summary>
        /// The page state while the viewer signs in by hand, plus whether they pressed Back (Escape) to give up. The key listener is put
        /// back on every read, since the sign-in pages navigate.
        /// </summary>
        public static string SignInWatchScript => @"(function() {
            if (!window.__bairdChannel4Back) {
                window.__bairdChannel4Back = true;
                window.addEventListener('keydown', (e) => {
                    if (e.key === 'Escape' || e.key === 'BrowserBack' || e.key === 'GoBack') { e.preventDefault(); window.__bairdChannel4Cancelled = true; }
                }, true);
            }
            const state = " + StateScript + @";
            state.cancelled = !!window.__bairdChannel4Cancelled;
            return state;
        })()";

        /// <summary>The page state and whether the viewer gave up (Back), or null when the page did not answer (it is navigating).</summary>
        public static async Task<(Channel4PageState State, bool Cancelled)?> WatchSignInAsync(ICdpPageDriver page, CancellationToken ct = default)
        {
            JsonElement? result;
            try { result = await page.EvaluateAsync(SignInWatchScript, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return null; }
            if (ParseState(result) is not { } state) return null;
            return (state, result!.Value.TryGetProperty("cancelled", out var c) && c.ValueKind == JsonValueKind.True);
        }

        public static Channel4PageState? ParseState(JsonElement? result)
        {
            if (result is not { ValueKind: JsonValueKind.Object } r) return null;
            return new Channel4PageState(
                Str(r, "url") ?? "",
                r.TryGetProperty("authenticated", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False ? a.GetBoolean() : null,
                Bool(r, "signInPrompt"), Bool(r, "emailField"), Bool(r, "passwordField"), Bool(r, "hasVideo"),
                Num(r, "currentTime"), Num(r, "duration"),
                !r.TryGetProperty("paused", out var p) || p.ValueKind != JsonValueKind.False,
                Bool(r, "ended"), Bool(r, "otherVideoPlaying"), Str(r, "text") ?? "", Num(r, "advertTime"), Bool(r, "playButton"));
        }

        public static async Task<Channel4PageState?> ReadStateAsync(ICdpPageDriver page, CancellationToken ct = default)
        {
            try { return ParseState(await page.EvaluateAsync(StateScript, ct)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return null; } // the page is navigating (sign-in redirects): no answer this time
        }

        /// <summary>
        /// True when the programme video is not what plays: another video plays, or the only video is an advert-length clip while the
        /// programme is known to be much longer.
        /// </summary>
        public static bool IsAdvert(Channel4PageState state, double programmeSeconds) =>
            state.OtherVideoPlaying ||
            (programmeSeconds > 0 && state.Duration > 0 && state.Duration < Math.Min(AdvertMaxSeconds, programmeSeconds * 0.5));

        /// <summary>
        /// JS that presses the page's Play button (an on-demand page does not always start the video by itself; measured on the Pi:
        /// signed in, the page showed "Play" and no video until it was pressed). A script click works on it. Returns "clicked" or null.
        /// </summary>
        public const string PressPlayScript = "(function() { const b = document.querySelector('button.text-button.play'); if (!b) return null; b.click(); return 'clicked'; })()";

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
            const end = dur(v) > 0 ? dur(v) - 1 : v.currentTime + Math.max(by, 0);
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

        private static string? Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static bool Bool(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        private static double Num(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    }
}
