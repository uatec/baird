# Channel 4 in Baird: findings and status

Status as of 2026-10-25. Built like the Disney+ integration (Disney+: [disney-spike-findings.md](disney-spike-findings.md)): Channel 4
programmes show in Baird's normal search and play through Baird's normal `VideoPlayer`, in the shared browser behind Baird's
transparent window (`IBrowserPlaybackEngine`).

## TL;DR

| Thing | State | Evidence |
|---|---|---|
| Search | **Works, no sign-in, no browser** | Public JSON API. Live test `BAIRD_CHANNEL4_LIVE=1` (dev PC, UK). |
| Series and episodes | **Works, no sign-in, no browser** | Programme page data (`window.__PARAMS__`). Live test: Derry Girls, 3 series, 19 episodes, each with a length. |
| Detecting "signed out" in the browser | **Works** | Live test: a signed-out on-demand page reads `isUserAuthenticated: false` and "Please sign in to watch"; the sign-in page shows its email field with an encoded `redirectUrl`. |
| Automated sign-in | **Blocked by Channel 4, removed** | Email check answered 401 `E_RECAPTCHA_THRESHOLD` (headless, dev PC); on the Pi an Akamai "Your Javascript is disabled" page. Baird must not try to get past it. |
| Sign-in on the TV ("Press OK to sign in") | **Works up to the typing** | Pi, test build, uinput keys: play → "Press OK to sign in to Channel 4" → OK shows Channel 4's sign-in page above Baird (screenshot) → Back returns to Baird ("sign-in cancelled", OK offers it again); done twice in a row. Signing in itself needs a person. |
| Sign-in from a computer (`channel4_signin.sh`) | **Built, not yet run** | The fallback: opens Channel 4 from Baird's profile on the Pi in a window on your screen (ssh -X). |
| "Not signed in" on the Pi | **Works** | Autotest `channel4-core` on the Pi (test build): search, series folders and episodes, then the player says how to sign in, 2 s after play. Screenshot checked. |
| Playback on the Pi | **Built, not tested** | Needs the manual sign-in first. |
| Adverts, length, seeking | **Guessed, not measured** | See "Not measured" below. |

## Configuration and sign-in

No config keys: Channel 4 is always in search (its search is public). To play, Baird's browser profile on the Pi must be signed in
to a (free) channel4.com account. **Channel 4's sign-in refuses automated browsers**, so Baird never signs in itself; the viewer does.

**On the TV (the usual way).** Playing a Channel 4 programme while signed out shows "Channel 4 is not signed in on this Baird. Press OK to
sign in to Channel 4, or Back to return." OK raises the shared browser above Baird on Channel 4's sign-in page (window focused, so keys
and the remote's pointer go to it; a keyboard is needed for the email and password). Baird only watches the page: when a page says
the viewer is signed in (`isUserAuthenticated`, after Channel 4 sends the browser back to the programme page), Baird comes back on top
and plays the programme. Back (Escape) on the page, the page's own close (X), or 10 minutes cancel it. Code:
`Channel4Service.SignInByHandAsync`, `IFailureActionEngine` (`Baird/Playback`), `VideoPlayer.RunFailureActionAsync`.

Trap found on the Pi: the OK that starts the sign-in can have its release delivered to the raised browser, and once Baird was back on
top it received a second OK nobody pressed (the sign-in page opened again). The failure screen now takes OK again only after Baird
has seen OK released.

**From a computer (fallback).** Run:

```bash
./channel4_signin.sh            # stops baird.service, opens the sign-in page from the Pi on your screen (ssh -X), restarts Baird when you close it
```

It uses Baird's profile (`~/.config/baird/browser-profile`) and `--password-store=basic`, like Baird's own browser, so Baird can
read the session. When the session expires, playing says "Channel 4 is not signed in on this Baird…": run the script again.

## What Channel 4 serves (observed 2026-10-25)

- **Search:** `GET https://all4nav.channel4.com/v1/api/search?expand=default&q=TERM&limit=N`, no auth. `results[].brand` with
  `title`, `websafeTitle` (the slug), `description`, `thumbnailUrl` (120 px; the image service resizes with `resize=480px:*`), and
  `label`, which is "No episodes available to watch on demand" for a programme with nothing to watch (Baird drops those). Only
  programmes (brands) are returned, and nothing says film or series: film descriptions start with the year, "(2007) Comedy. ...",
  which Baird uses as a heuristic (`Channel4SearchResult.LooksLikeFilm`). A wrong guess still plays.
- **Programme page:** `https://www.channel4.com/programmes/{slug}` has `window.__PARAMS__ = {...}`, a JS object literal (JSON
  plus bare `undefined`). `initialData.brand.programmeType` is `FM` for a film, `MST` for a series. `brand.episodes[]` holds every
  episode of every series (326 for Gogglebox, so no paging): `programmeId` ("59741-001"), `seriesNumber`, `episodeNumber`, `title`,
  `summary`, `image.src`, `durationLabel` ("23 mins") and `hrefLink`. An episode that is not out yet has no `hrefLink` and no length.
  A film's one episode has an empty `title`.
- **Play page:** `https://www.channel4.com/programmes/{slug}/on-demand/{programmeId}`. Its `__PARAMS__.initialData` has
  `isUserAuthenticated`. Signed out it shows "Please sign in to watch" with Sign In / Register buttons instead of the player.
- **Sign-in:** `https://www.channel4.com/sign-in-or-register?redirectUrl=...`: `#email-field` and "Let's go", then a password page.
  "Let's go" sends `POST /identity/check-email` with `{email, captchaToken}` (reCAPTCHA Enterprise). From a CDP-driven browser it
  answered **401 `{"error":{"code":"E_RECAPTCHA_THRESHOLD"}}`** ("Sorry, something went wrong. Please try again or use a different
  browser.") on the dev PC, and an Akamai Bot Manager page ("Your Javascript is disabled") on the Pi. This is bot detection, so
  Baird does not automate it (see above).
- The cookie banner's "Accept & Continue" (`.all4-cc-primary-button`) accepts `click()`.
- `/search?q=` and `/login` are 404s.

## How it is built

| Piece | Where | What it does |
|---|---|---|
| `Channel4Catalogue` | `Baird/Services/Channel4Catalogue.cs` | Search and programme pages over plain HTTP; pure parsers (search reply, `__PARAMS__` literal, "23 mins"); programme pages reused for 10 min. |
| `Channel4Provider` | `Baird/Services/Channel4Provider.cs` | Source "Channel 4". A series is a `Brand` (`channel4:{slug}`): series folders `channel4:{slug}\|{n}` (newest first) or the episodes when there is one series; episodes are `channel4:ep:{programmeId}` with their on-demand URL. A film is a `Video` whose stream URL is its programme page. Works while another service plays. |
| `Channel4Service` | `Baird/Services/Channel4Service.cs` | The shared browser (`BrowserHub`): takes the tab and marks Channel 4 as playing, returns the tab to `https://www.channel4.com/robots.txt`. Never signs in. No start-up warm-up. |
| `channel4_signin.sh` | repo root, run on your computer | The one-time manual sign-in in Baird's profile on the Pi (ssh X forwarding). |
| `Channel4PlaybackEngine` | `Baird/Services/Channel4PlaybackEngine.cs` | Opens the on-demand page, fails with the sign-in instructions if the page asks for a sign-in, waits (90 s) for a video whose time advances, applies the page style, polls every 500 ms, and ends the title when the video ends or the page moves to another programme (Baird then plays its own next episode). |
| `Channel4PlayerControl` | `Baird/Services/Channel4PlayerControl.cs` | The page-state, pause/play and seek scripts. |
| Page style | `Baird/Resources/channel4-page-style.js` | Hides everything on the page but the playing `<video>`, which fills the window; hides the pointer. |
| Autotest | `BAIRD_AUTOTEST=channel4-core` | Search (`BAIRD_AUTOTEST_C4_QUERY`, default "Derry Girls"), open the first series, play its first episode, then log position, length and advert every 10 s for a minute. |

## Not measured (needs the Pi signed in)

Everything about the player was written from standard `<video>` behaviour, not from Channel 4's page:

- **Adverts.** Channel 4's free tier has pre-roll and mid-roll adverts. The engine treats "another video plays" or "the only video is
  under 4 minutes while the programme is much longer" as an advert: the position and length stay the programme's, and seeking is
  refused. If the adverts are stitched into the programme stream, `video.currentTime` includes them and the HUD will be off.
- **Length.** The page's `video.duration` is used when known, else the programme page's minutes. Disney+'s `video.duration` turned
  out to be the buffered end, so check this.
- **Seeking** sets `video.currentTime` on the programme video. Disney+'s native seek could stall; Channel 4's may refuse to skip adverts.
- **Prompts** ("Next episode" countdown, "Continue watching?") are not read; `IPromptingEngine` is not implemented.
- **Subtitles** drawn by the page are hidden by the page style; `SetSubtitle` does nothing.
- **Widevine**: assumed to work like Netflix and Disney+ in the shared profile (L3, standard definition).

## How to run

```bash
# Unit tests
dotnet test Baird.Tests/Baird.Tests.csproj --filter "FullyQualifiedName~Channel4"
# Against the real site (UK, anonymous; the browser test needs Chrome)
BAIRD_CHANNEL4_LIVE=1 BAIRD_CHROME_EXTRA_ARGS="--headless=new" dotnet test Baird.Tests/Baird.Tests.csproj --filter "FullyQualifiedName~Channel4LiveTests"
# On the Pi, after ./channel4_signin.sh (tell the other Baird agents first: it stops baird.service)
spikes/netflix-tooling/deploy_test_build.sh
ssh -o HostKeyAlias=baird baird.local 'bash ~/baird-test/pi_autotest.sh channel4-core test'
```
