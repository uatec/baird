using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Baird.Services
{
    /// <summary>What the Disney+ page shows right now.</summary>
    public sealed record DisneyPageStatus(string Url, string Title, bool HasLoginInput, bool HasProfilePicker, bool IsVideo,
        double CurrentTime, double Duration, bool Paused, int VideoWidth, string Text, bool HasAppNav = false)
    {
        public DisneyPageKind Kind => DisneyPageClassifier.Classify(Url, HasLoginInput, HasProfilePicker, IsVideo, CurrentTime, HasAppNav);
    }

    /// <summary>
    /// Drives Disney+ in the shared hidden Chromium: reads page state, signs in with the configured credentials (email, then
    /// "Enter a password instead", then password), picks a profile, and calls the search API with the web app's own token.
    /// Steps and selectors were observed on the live site. The password is typed with CDP Input.insertText and never logged.
    /// </summary>
    public sealed class DisneySession
    {
        public const string LoginUrl = "https://www.disneyplus.com/identity/login/enter-email";

        /// <summary>
        /// How long a page that is none of login / profile picker / signed-in shell / playing video may stay before it counts as
        /// "signed out". A signed-in /play page legitimately sits on a spinner for ~15 s (measured on the Pi).
        /// </summary>
        public TimeSpan UnknownGrace { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// A signed-out visitor who opens an app route (e.g. /browse/search) is NOT redirected to the login page: Disney+ shows its
        /// "page cannot be found" page (measured on the Pi). That page is proof of being signed out.
        /// </summary>
        public static bool IsNotFoundPage(string? text) => text?.Contains("cannot be found", StringComparison.OrdinalIgnoreCase) == true;

        private readonly ICdpBrowserService _browser;
        private readonly ICdpPageDriver _page;
        private readonly Action<string> _log;

        public DisneySession(ICdpBrowserService browser, ICdpPageDriver page, Action<string> log)
        {
            _browser = browser;
            _page = page;
            _log = log;
        }

        /// <summary>
        /// Disney+ renders TWO &lt;video&gt; elements: the first stays empty (readyState 0) and the second plays, so pick the one
        /// that has a source. Also accepts the OneTrust cookie banner so it never covers the TV.
        /// </summary>
        private const string StateScript = @"(function() {
            try { const ob = document.querySelector('#onetrust-accept-btn-handler'); if (ob) ob.click(); } catch (e) { }
            const v = [...document.querySelectorAll('video')].find(x => x.readyState > 0 && x.currentSrc) || null;
            return {
                url: location.href,
                title: document.title || '',
                hasLoginInput: !!document.querySelector('input[type=email], input[type=password]'),
                hasProfilePicker: !!document.querySelector('[data-testid^=""profile-avatar-""]'),
                hasAppNav: !!document.querySelector('[data-testid^=""navigation-item-""]'),
                isVideo: !!v,
                currentTime: v ? v.currentTime : 0,
                duration: v && isFinite(v.duration) ? v.duration : 0,
                paused: v ? v.paused : true,
                videoWidth: v ? v.videoWidth : 0,
                text: (document.body ? document.body.innerText : '').replace(/\s+/g, ' ').slice(0, 240)
            };
        })()";

        /// <summary>
        /// Evaluates a script, treating a CDP error as "no answer". Disney+ navigates itself during sign-in (login, profile loading,
        /// profile picker, home) and CDP then answers "Inspected target navigated or closed"; that must not end the flow.
        /// </summary>
        private async Task<JsonElement?> TryEvaluateAsync(string script, CancellationToken ct)
        {
            try { return await _page.EvaluateAsync(script, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log("[CDP] Page script failed (page probably navigating): " + ex.Message);
                return null;
            }
        }

        public async Task<DisneyPageStatus?> ReadStateAsync(CancellationToken ct)
        {
            var r = await TryEvaluateAsync(StateScript, ct);
            if (r is not { ValueKind: JsonValueKind.Object } o) return null;
            return new DisneyPageStatus(
                Str(o, "url") ?? "", Str(o, "title") ?? "",
                Bool(o, "hasLoginInput"), Bool(o, "hasProfilePicker"), Bool(o, "isVideo"),
                Num(o, "currentTime"), Num(o, "duration"), Bool(o, "paused"), (int)Num(o, "videoWidth"), Str(o, "text") ?? "", Bool(o, "hasAppNav"));
        }

        /// <summary>
        /// Signs in (once) and picks a profile until the page shows a signed-in app page. Returns null when ready, else a
        /// "FAILED: ..." reason that says what was on screen.
        /// </summary>
        public async Task<string?> EnsureSignedInAsync(DisneyCredentials? credentials, string? profileName, TimeSpan timeout, TimeSpan poll, CancellationToken ct)
        {
            bool signInTried = false, profileChosen = false;
            DisneyPageStatus? last = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var unknownSince = TimeSpan.Zero;
            bool wasUnknown = false, loggedSignedOut = false;
            while (sw.Elapsed < timeout)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(poll, ct);
                var st = await ReadStateAsync(ct);
                if (st == null) continue;
                last = st;

                var kind = st.Kind;
                if (kind == DisneyPageKind.Unknown)
                {
                    if (!wasUnknown) { wasUnknown = true; unknownSince = sw.Elapsed; }
                    if (IsNotFoundPage(st.Text) || sw.Elapsed - unknownSince > UnknownGrace)
                    {
                        if (!loggedSignedOut) _log("[Auth] Page is not a Disney+ app page (" + (IsNotFoundPage(st.Text) ? "not-found page" : "no recognisable page for " + UnknownGrace.TotalSeconds + " s") + "): treating the profile as signed out.");
                        loggedSignedOut = true;
                        kind = DisneyPageKind.LoginRequired;
                    }
                }
                else wasUnknown = false;

                switch (kind)
                {
                    case DisneyPageKind.LoggedIn:
                    case DisneyPageKind.VideoPlaying:
                        _log("[Auth] Signed in to Disney+ (" + st.Url + ").");
                        return null;

                    case DisneyPageKind.ProfilePicker:
                        if (profileChosen) break; // still transitioning
                        var chosen = await SelectProfileAsync(profileName, ct);
                        if (chosen == null) break; // the URL changes before the tiles render (measured on the Pi): try again next poll
                        profileChosen = true;
                        _log($"[Auth] \"Who's watching?\" shown; chose {chosen}" + (profileName == null ? " (set DISNEY_PROFILE in config.ini to choose)." : "."));
                        break;

                    case DisneyPageKind.LoginRequired:
                        if (credentials == null)
                            return $"FAILED: not signed in to Disney+ and no {DisneyCredentials.UserKey}/{DisneyCredentials.PasswordKey} in ~/.baird/config.ini.";
                        if (signInTried)
                        {
                            // The form is still showing after our one attempt (or we are mid-redirect): give it a moment, then fail loudly.
                            if (sw.Elapsed > timeout / 2 && st.HasLoginInput)
                                return $"FAILED: signing in as {credentials.User} did not complete. Disney+ still shows the login form: \"{st.Text}\"";
                            break;
                        }
                        signInTried = true;
                        var fail = await SignInAsync(credentials, ct);
                        if (fail != null) return "FAILED: " + fail;
                        break;
                }
            }
            return $"FAILED: no signed-in Disney+ page after {timeout.TotalSeconds:F0}s. Last page: {last?.Url ?? "unreadable"} \"{last?.Text}\"";
        }

        /// <summary>One sign-in attempt. Returns null when the form was submitted, else why it could not be.</summary>
        private async Task<string?> SignInAsync(DisneyCredentials creds, CancellationToken ct)
        {
            _log($"[SignIn] Signing in as {creds.User} (password not logged).");
            if (!await _browser.NavigateAsync(LoginUrl, ct)) return "could not open the Disney+ login page over CDP.";

            // A session that is already valid is redirected away from the login page (to /profiles/loading, /select-profile, /home).
            // Wait for the email field OR for that redirect; if redirected, there is nothing to type and the caller keeps polling.
            if (!await WaitForAsync("!!document.querySelector('input[type=email]') || (location.pathname.indexOf('/identity/') < 0 && document.readyState === 'complete')", ct))
                return "the email field did not appear on the login page.";
            var emailShown = await TryEvaluateAsync("(function(){return !!document.querySelector('input[type=email]');})()", ct);
            if (emailShown is not { ValueKind: JsonValueKind.True })
            {
                _log("[SignIn] The login page redirected away: the session is already signed in. Nothing to type.");
                return null;
            }
            if (!await _page.TypeIntoAsync("input[type=email]", creds.User, ct)) return "could not type into the email field.";
            await ClickAsync("[data-testid=continue-btn]", ct);

            // Next: "We need to confirm you're you" (one-time code by default) with an "Enter a password instead" button.
            if (!await WaitForAsync("!!document.querySelector('[data-testid=passwordless-login-with-pwd-btn], input[type=password]')", ct))
                return "after the email, neither a password field nor \"Enter a password instead\" appeared (is a one-time code or captcha required?).";
            await ClickAsync("[data-testid=passwordless-login-with-pwd-btn]", ct);

            if (!await WaitForAsync("!!document.querySelector('input[type=password]')", ct)) return "the password field did not appear.";
            if (!await _page.TypeIntoAsync("input[type=password]", creds.Password, ct)) return "could not type into the password field.";
            var clicked = await TryEvaluateAsync(
                "(function(){const b=[...document.querySelectorAll('button[type=submit]')].find(x=>/log\\s*in/i.test(x.innerText));if(!b)return false;b.click();return true;})()", ct);
            if (clicked is not { ValueKind: JsonValueKind.True }) await _page.PressEnterAsync(ct);
            _log("[SignIn] Password submitted.");
            return null;
        }

        private async Task<bool> WaitForAsync(string condition, CancellationToken ct, int tries = 40)
        {
            for (int i = 0; i < tries; i++)
            {
                ct.ThrowIfCancellationRequested();
                var r = await TryEvaluateAsync($"(function(){{return {condition};}})()", ct);
                if (r is { ValueKind: JsonValueKind.True }) return true;
                await Task.Delay(500, ct);
            }
            return false;
        }

        private async Task ClickAsync(string selector, CancellationToken ct)
        {
            var sel = JsonSerializer.Serialize(selector, BairdJsonContext.Default.String);
            await TryEvaluateAsync($"(function(){{const e=document.querySelector({sel});if(e)e.click();}})()", ct);
        }

        /// <summary>Clicks the named profile tile (aria-label "Sign in to NAME's profile."), else the first. Returns the name clicked.</summary>
        public async Task<string?> SelectProfileAsync(string? preferredName, CancellationToken ct)
        {
            var wanted = JsonSerializer.Serialize(preferredName ?? "", BairdJsonContext.Default.String);
            var r = await TryEvaluateAsync(@"(function() {
                const wanted = " + wanted + @".trim().toLowerCase();
                const nameOf = e => (e.getAttribute('aria-label') || '').replace(/^Sign in to /i, '').replace(/'s profile\.?$/i, '').trim();
                const tiles = [...document.querySelectorAll('[data-testid^=""profile-avatar-""]')];
                if (!tiles.length) return null;
                const tile = (wanted && tiles.find(t => nameOf(t).toLowerCase() === wanted)) || tiles[0];
                tile.click();
                return nameOf(tile);
            })()", ct);
            return r is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
        }

        // The web app's API headers (bearer token included), captured once and reused. Never logged.
        private JsonObject? _apiHeaders;

        /// <summary>True when a token is cached, so the next API call needs no page visit.</summary>
        public bool HasApiToken => _apiHeaders != null;

        /// <summary>Takes the token now if none is cached (leaves the page on the search page). False when none could be captured.</summary>
        public async Task<bool> EnsureApiTokenAsync(CancellationToken ct)
        {
            _apiHeaders ??= await CaptureApiHeadersAsync(ct);
            return _apiHeaders != null;
        }

        /// <summary>Forget the cached token (a new sign-in, or Disney+ rejected it).</summary>
        public void InvalidateApiToken() => _apiHeaders = null;

        /// <summary>
        /// Opens the web app's own search page and reads the headers of the first explore API request it sends: the token the
        /// web app keeps in memory (no cookie or storage entry holds it). Returns null when no such request was seen.
        /// </summary>
        private async Task<JsonObject?> CaptureApiHeadersAsync(CancellationToken ct)
        {
            var captured = await _page.NavigateAndCaptureAsync(DisneySearchParser.SearchPageUrl,
                r => r.Url.StartsWith(DisneySearchParser.ExplorePrefix, StringComparison.Ordinal) &&
                     r.Headers.ContainsKey("authorization"),
                TimeSpan.FromSeconds(25), ct);
            if (captured == null) return null;
            _log("[Api] Captured the web app's API headers (token not logged).");
            var headers = new JsonObject();
            foreach (var name in new[] { "authorization", "x-application-version", "x-bamsdk-client-id", "x-bamsdk-version", "x-bamsdk-platform", "x-dss-edge-accept", "accept" })
                if (captured.Headers.TryGetValue(name, out var value)) headers[name] = value;
            return headers;
        }

        /// <summary>One GET from inside the page with the given headers; (0, null) when the request itself failed (CORS, network).</summary>
        private async Task<(int Status, string? Body)> FetchAsync(string url, JsonObject headers, CancellationToken ct)
        {
            var urlLiteral = JsonSerializer.Serialize(url, BairdJsonContext.Default.String);
            var script = @"(async function() {
                try {
                    const r = await fetch(" + urlLiteral + @", { headers: " + headers.ToJsonString() + @" });
                    return JSON.stringify({ status: r.status, body: await r.text() });
                } catch (e) { return JSON.stringify({ status: 0, error: String(e) }); }
            })()";
            var reply = await _page.EvaluateAsync(script, ct);
            if (reply is not { ValueKind: JsonValueKind.String } s) return (0, null);
            using var doc = JsonDocument.Parse(s.GetString()!);
            return ((int)Num(doc.RootElement, "status"), Str(doc.RootElement, "body"));
        }

        /// <summary>
        /// GET <paramref name="url"/> on Disney's API from inside the signed-in page. Uses the cached token; with none, or when
        /// Disney+ answers 401/403 or the request fails, captures a fresh one from the web app (at most once) and retries.
        /// Returns the failure when no token could be captured.
        /// </summary>
        public async Task<(int Status, string? Body, string? Failure)> ApiGetAsync(string url, CancellationToken ct)
        {
            (int Status, string? Body) last = (0, null);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (_apiHeaders == null)
                {
                    _apiHeaders = await CaptureApiHeadersAsync(ct);
                    if (_apiHeaders == null) return (0, null, "could not capture the Disney+ web app's API request (no explore call seen within 25 s).");
                }
                last = await FetchAsync(url, _apiHeaders, ct);
                if (last.Status != 0 && last.Status is not (401 or 403)) return (last.Status, last.Body, null);
                _log($"[Api] Request answered {(last.Status == 0 ? "with no reply" : last.Status.ToString())}; capturing a fresh token" + (attempt == 0 ? " and retrying." : "."));
                _apiHeaders = null;
            }
            return (last.Status, last.Body, null);
        }

        /// <summary>Searches the signed-in account's catalogue: GET /explore/v1.21/search from inside the page.</summary>
        public async Task<DisneySearchReply> SearchAsync(string term, CancellationToken ct)
        {
            var (status, body, failure) = await ApiGetAsync(DisneySearchParser.SearchApiUrl(term), ct);
            if (failure != null) return new DisneySearchReply(Array.Empty<DisneySearchResult>(), 0, failure);
            return DisneySearchParser.Parse(status, body);
        }

        /// <summary>What to play for an entity (from search): its play id, length and a HUD label. Null with the reason on failure.</summary>
        public async Task<(DisneyPlayTarget? Target, string? Failure)> ResolvePlayTargetAsync(string entityId, CancellationToken ct)
        {
            var (status, body, failure) = await ApiGetAsync(DisneySearchParser.EntityApiUrl(entityId), ct);
            if (failure != null) return (null, failure);
            var target = DisneySearchParser.ParseEntityPlayTarget(status, body, out var parseFailure);
            return (target, parseFailure);
        }

        private static string? Str(JsonElement o, string n) => o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static bool Bool(JsonElement o, string n) => o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
        private static double Num(JsonElement o, string n) => o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    }
}
