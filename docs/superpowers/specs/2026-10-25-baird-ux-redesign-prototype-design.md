# Baird UX redesign — working HTML prototype

**Date:** 2026-10-25 · **Status:** design for review · **Deliverable:** a clickable, remote-driven HTML prototype served on
the dev PC's Tailscale IP. It is not a change to the Avalonia app.

## Intent

Baird is a living-room TV box (Raspberry Pi 5, Avalonia UI) that combines UK broadcast TV (DVB via TvHeadend, with an
EPG) with on-demand sources: Jellyfin, BBC iPlayer, YouTube, Netflix, Disney+, and requesting new titles through
Jellyseerr/Seerr. Today it shows these as nine-plus tabs (History, Guide, Search, Watchlist, Discover, Requests,
Settings, CEC Debug, A/V Debug) that were built one capability at a time. In practice search is the only way in: on a
typical evening the household watched several Simpsons episodes, one Loki, and half of Casper, all found by searching.

The redesign should make Baird feel like it belongs with Apple TV: premium and calm, with one obvious thing to do
next. It should treat **live TV and on-demand as equal partners**, without pretending they're the same. The prototype
exists so the owner can feel the flows with a remote, and so we can later choose an animation approach (Lottie vs Rive)
that suits the design.

## Goals

1. **One catalogue, no duplicates.** Each show or film has a single identity (TVDB/TMDB, possibly via Seerr). Every
   service's copy of it hangs off that one page. A show you can watch offers ▶ Play. A show Seerr knows but you don't
   have offers ⤓ Get it. Search and discovery look and behave the same in both cases.
2. **Getting in works the same everywhere.** Live channels and on-demand titles look and behave differently, but
   entering them is identical: every card responds to OK (play/tune) and hold OK (actions) the same way.
3. **Up Next.** Finishing an episode offers the next one. Half-watched things wait in Up Next.
4. **A first-class TV guide.** It's easy to scan down to a channel (or type its number) and back and forward in time,
   with clear routes from the guide into on-demand.
5. **A spatial model you can feel.** You always know where you are and how to get back.
6. **Premium visual design** on par with Apple TV: real artwork, confident type, restrained colour, smooth motion.

## Non-goals

- Changing the Avalonia app, its services, or the Pi. The prototype is standalone HTML.
- Real playback, real tuning, real Seerr requests, or real metadata lookups. Everything is mocked.
- Settings beyond a believable placeholder. CEC and A/V debug only need a reachable entry point.
- Mouse or touch. The pointer is off on the real device.
- Multi-user profiles, parental controls, recordings/PVR.

## Anti-goals (fail even if it "works")

- **A Google TV look-alike.** Rows are fine (the owner liked them), but generic cards on a flat background read as
  Google TV and "not very premium". The premium must come from artwork, type, light and motion.
- **Forcing linear time onto on-demand**, or dressing on-demand up as channels. Both were proposed and rejected.
- **Hiding the difference** between live TV and on-demand.
- **Two things lit at once.** Exactly one element has focus, and Right/Left/Up/Down always have one obvious target.
- **Arrow keys opening panels.** Arrows move. Panels open with specific buttons (see the remote map).
- **A guide tile that grows** over its neighbours when lit. This was rejected. A lit guide cell keeps its size.
- A "revolutionary" guide that loses the classic grid's at-a-glance view of many channels.

## Constraints

- **Screen:** designed for 1920×1080 on a TV viewed from a sofa. The prototype scales to fit any browser window at
  16:9 (letterbox if needed). Minimum body text is about 24 px at 1080p. Respect TV safe areas (about 5% margins).
- **Input:** remote only. The physical remote is a G20S Pro air-mouse remote (photo in this conversation; keys below).
  In the browser, keyboard keys stand in for remote buttons. The on-screen remote legend (see "What's left") shows the
  mapping.
- **Two target browsers:** the owner will try it full-screen on the dev PC *and* in the Raspberry Pi 5's Chromium on the
  TV. It must stay smooth on the Pi, whose GPU is weak and already busy. Use transform/opacity-only animation, limit
  simultaneous `backdrop-filter` layers (or fall back to pre-blurred/tinted panels on the Pi: detect `aarch64`/Linux ARM
  in the UA or a `?lite` flag), cap image sizes to what's on screen (backdrops ≤ 1920 px, cards ≤ 500 px), and avoid
  per-frame JS layout.
- **Real remote keys:** the G20S Pro is a USB HID keyboard/consumer-control device. Accept the keyboard stand-ins *and*
  the codes it likely sends (`MediaPlayPause`, `MediaTrackPrevious/Next`, `PageUp/PageDown`, `BrowserBack`/`Escape`/
  `Backspace` for Back, `BrowserHome`/`HomePage` for Home, `ContextMenu` for ≡, and so on). Prevent the browser's own back
  navigation (`BrowserBack`, Backspace) from leaving the page. Include a key-inspector overlay (toggle with a debug key)
  that shows the last `key`/`code` received, so the true mapping can be confirmed on the Pi.
- **Hosting:** static files (HTML/CSS/JS, no build step, no framework required) served on the dev PC's Tailscale IP
  `100.109.40.12`, on a free port, so the owner can open it remotely. Keep the files under
  `spikes/ux-redesign-prototype/` in this repo. Do not commit without the owner's say-so.
- **Artwork:** the TMDB image CDN (`https://image.tmdb.org/t/p/...`) is reachable from the dev PC. Real
  posters/backdrops/stills are strongly preferred; placeholder gradients were called "simplistic". Download chosen
  images into the prototype folder (so it works offline and leaks no API keys to the browser). If a title's art can't
  be found, use a tasteful generated fallback, never a flat grey box.
- **Brand restraint:** red means live/now; gold means "you watch this show". Everything else is neutral, and the artwork
  provides the colour.

## Remote map (decided)

| Remote button | Keyboard stand-in | Watching live TV | Watching on-demand | Elsewhere |
|---|---|---|---|---|
| OK | Enter | Show info bar | Show info bar | Select (play/tune/open) |
| hold OK (≥ 500 ms) | hold Enter | Actions panel | Actions panel | Actions panel for the lit item |
| ▴ ▾ | ↑ ↓ | Channel list (tunes when you rest ~1 s; OK tunes now) | nothing | Move |
| ◂ ▸ | ← → | nothing | nothing | Move |
| Pg+ / Pg− | PageUp / PageDown | Channel up/down immediately (no list) | nothing | Page through guide/lists |
| ≡ (bulb/lines key) | G | Open the Guide | Open the Guide | Open the Guide |
| ⏯ | Space | Pause/play (live is time-shifted) | Pause/play | Play the lit item |
| ⏮ / ⏭ | `[` / `]` | Back 10 s / forward 30 s (hold = scrub) | Same; on the end card ⏭ = next episode now | — |
| 0–9 | 0–9 | Tune by number (shows "1 0 _" then tunes) | Tune by number | Guide: jump to channel; Search: type |
| DEL | Delete | Correct a typed channel number | — | Search: backspace |
| Voice | V | Voice search (mock: fills in a query) | Same | Same |
| Back | Escape or Backspace | Close the overlay; on the bare video → the page you came from | Same | One step back towards Home |
| Home | H | Home | Home | Home |

Whether the real ≡ key sends a code Baird can see must be checked on the Pi later. It doesn't affect the prototype.

## Spatial model (decided)

The **video is the ground floor**. It always runs underneath once something is playing. Everything else is a layer that
arrives from a fixed direction:

- **Home** slides down from the top, over the video.
- **Channel list** (live only) is a narrow strip on the left.
- **Actions panel** (hold OK) comes in from the right, as glass over the content.
- **Info bar** and the **Guide** rise from the bottom. The Guide shows the current video in its top-right preview
  window.

**Back retraces the journey (owner's rule):**
- Over the video, Back closes whatever is on top of it (actions panel, channel list, info bar/progress bar, end cards)
  and returns to the playing video.
- In the Guide, Search, a show page or Settings, Back steps back through the navigation hierarchy, one page at a time,
  to the Home screen.
- On the bare video, Back returns to the page the video was started from (for example Search → show page → play →
  Back returns to the show page), and further Backs continue to Home. If the video was started from Home, Back goes Home.
- On Home, Back does nothing. The playing picture stays visible as Home's hero backdrop (live channel or paused show)
  and in the Guide's preview, so the household never loses what they were watching.

When nothing has been played yet (power-on), the ground floor is empty and Home is what you see. **Power-on shows Home.**

**Pages above Home (my call):** Search, Show page, Settings and the Guide are full-screen layers stacked over Home (or
over the video, if opened from it). Back pops one at a time and restores focus to where it was. The top nav is a
shortcut that opens these as layers; it is not a set of peer tabs. Playing or tuning puts the video on screen and
remembers the page stack beneath it, so Back can retrace the journey as above.

**Overlays that take focus while watching (my call):** the info bar, once raised, is focusable. ◂ ▸ move between its
buttons (⏯ · Subtitles · Audio), OK presses, and Back or 5 s idle dismisses it. ▴ ▾ dismiss it and open the channel
list, as on the bare video. The on-demand end card and the near-end live card take focus when they appear: OK does their
primary action (Play now / Stay), ◂ ▸ choose between their two buttons, and Back dismisses them. Pg+/Pg−, digits, ⏯,
⏮/⏭ and ≡ keep their global meaning whatever overlay is up.

## Information architecture

Three top-level places plus a gear: **Home · Live TV (the Guide) · Search**, and **⚙ Settings**. They sit in a small top
navigation on Home. ≡ opens the Guide from anywhere, and Home/H returns Home.

What happens to today's tabs:

| Today | Becomes |
|---|---|
| History | **Up Next** row on Home: the next episode of each show in progress, plus anything half-watched. Finished films drop off. |
| Guide | **Live TV**: the grid guide (below), and the **On Now** row on Home |
| Search | **Search**: one search across on-demand titles, channels and guide listings |
| Watchlist | **Watchlist** row on Home |
| Discover (Seerr trending) | A **Discover** row on Home ("can get" titles mixed with "can play now" recommendations) |
| Requests | Gone as a page. A requested title appears in **Up Next** with download progress ("⤓ 62%") and becomes playable when it lands. |
| Settings, CEC Debug, A/V Debug | **Settings** (gear). Debug pages are entries inside Settings. |

## Screens and behaviour

### Home (decided in outline)

- A large **hero** at the top. It describes the lit card (title, episode, one-line synopsis, source badge). If something
  is playing, the live picture/backdrop runs behind it. The hero has **no buttons of its own**, so focus is never split.
  OK on a card plays it; hold OK opens its actions.
- Rows, in order: **Up Next**, **On Now** (live channels: channel name/number, current programme, progress), **Watchlist**,
  **Discover**. Further rows are the builder's call (e.g. "New on Jellyfin", "Films tonight on TV").
- Focus starts on the first Up Next card. Up from the first row reaches the top navigation.

### Show page (my call: owner hasn't reviewed it)

One page per show or film, whatever services carry it. Reached from any card via hold OK → "Open the series", or via OK
on a search result for a show.
- Backdrop art, title, year/type/rating, synopsis.
- Primary button: **▶ Resume S1 E3** / **▶ Play** / **⤓ Get it**, or a status ("Requested · ⤓ 62%", "Available soon").
- When a title is on several services, Baird picks the source quietly: your own Jellyfin copy first, then a service
  you're signed into. A small "Also on: Disney+, iPlayer" line shows the alternatives, and the actions panel can switch.
- For series: a season picker and an episode list with stills, progress and "watched" marks.
- If the show also airs on TV: "Next on TV: Tomorrow 18:00, Channel 4" (OK goes to that slot in the Guide).

### Search (my call: owner hasn't reviewed it)

- One input at the top with the on-screen keyboard (letters plus a digit row, as today) and voice (V). Results update
  as you type.
- Results are grouped, in this order: **Top result**, **Shows & films** (one card per title, with a ▶ or ⤓ badge),
  **Channels** (matching names/numbers), **On TV** (matching guide listings with time and channel, including "↺ catch-up"
  where available).
- OK on a show goes to its page. OK on a channel tunes it. OK on a listing plays it if it's on now or available on
  catch-up; otherwise it opens the Guide at that slot.
- Example to support in the mock data: "simpsons" returns the show (on Jellyfin), Channel 4's 18:00 broadcast, and
  catch-up. "severance" returns a ⤓ Get it title.

### Live TV: the Guide (decided)

- **The classic grid:** channels down the left (number and name), time across, with a red NOW line. Earlier
  programmes are dimmed. The top area describes the lit programme (channel · time · title · badges · synopsis), and the
  **live preview** of the current video sits top-right.
- A **day/time jump strip** (Now · Tonight 21:00 · Tomorrow · next days) moves time without holding → for ages.
- **Lit cell:** same size as its neighbours. Lit by fill and contrast only (white fill, dark text), as in the "calm grid"
  mockup. **No growing tile.**
- Badges: **↺** on earlier programmes available on catch-up (OK plays from the start). Gold **YOU WATCH** on any
  broadcast of a show you watch on-demand.
- Navigation: ↑↓ move a channel, ←→ move a programme, Pg+/Pg− a page of channels, digits jump to a channel. OK on now
  = tune; OK on earlier+↺ = play catch-up; OK on later = open actions with "Remind me" highlighted.
- **Hold OK actions panel** (right side): Watch / Watch from the start (catch-up) · **Open the series** (show page:
  stream or get) · **Next showing on TV** (the grid glides to that slot, even on another channel/day) · **This
  channel's schedule** (a 7-day single-channel list; Back returns to the same grid position) · Remind me · Add to
  Watchlist.

### Watching live TV (decided)

- **OK → info bar** (bottom, glass): channel number/name, LIVE badge, title, progress with the live edge marked red
  and "N min behind live" when time-shifted, buttons for ⏯ / Subtitles / Audio, and "Next: 18:30 Regional News". It
  fades after 5 s.
- **↑↓ → channel list** (left strip): each press moves one channel and shows what's on now. It tunes after resting
  about 1 s; OK tunes immediately.
- **Pg+/Pg−** change channel immediately, with a brief channel banner.
- **Digits** show the typed number with the matching channel lit, then tune after a short pause. DEL corrects.
- **Near the end of a programme** (last ~5 min), a small corner card says "Up next on BBC One · 18:30 Regional News, then
  19:00 EastEnders (YOU WATCH)", with OK = stay and ↑↓ = find something else.
- **Hold OK → actions panel:** Subtitles (first, because it's used most), Start over, This channel's schedule, Open the
  series, Audio, Add channel to favourites.

### Watching on-demand (decided by analogy)

- Same info bar, without channel elements. Scrub with ⏮/⏭.
- **End of an episode:** an "Up next · in 8 s" card (thumbnail, S1 E4 title) with ▶ Play now / Back to show.
  Countdown autoplay.
- End of a film: return to Home with the film removed from Up Next.

### Settings (placeholder)

A simple, premium list: UI scale, subtitles default, sources (connected services), and Debug (CEC, A/V). It only needs
to look right and be reachable.

## Visual direction

- **Reference:** Apple TV (tvOS) home and show pages. Not Google TV.
- **Artwork first:** full-bleed backdrops with scrims that make text legible. Posters/stills on cards. Art supplies the
  colour; the chrome is neutral.
- **Type:** a system/SF-style sans, large confident titles (tight tracking), clear hierarchy. Use the `-apple-system`
  stack with a sensible fallback (e.g. Inter, if bundled locally).
- **Glass:** overlays (info bar, actions panel, channel list) are frosted glass over the video.
- **Focus:** the lit card lifts slightly (≤ ~1.08 scale on Home cards, none in the guide), with a soft shadow and a
  subtle light sheen. One focus at a time.
- **Motion:** 200–350 ms ease-out for focus moves and layer transitions. Layers slide from their spatial direction.
  Rows scroll so focus stays near a fixed position rather than the highlight running off-screen.
- **Colour:** red only for LIVE/NOW, gold only for YOU WATCH. No other accent colours unless the art provides them.
- Mockups from this conversation are in `.superpowers/brainstorm/*/content/` (`epg-directions.html` A = the accepted
  grid; `watching-live.html` = the accepted live-TV layers). `home-screen.html` shows the accepted row idea, but its
  look was rejected.

## The "video" and animation (my call)

- **No real video.** The ground floor is the title's or channel programme's backdrop still, with slow Ken Burns drift and
  a faint film grain, so it reads as "something playing". Playback progress is simulated by a clock. A debug key
  fast-forwards to the last 10 s so the end cards can be shown.
- **Motion is CSS/JS only.** Lottie or Rive will be chosen *after* this design is seen. For that comparison, the
  prototype should list (in its README) the moments where richer animation would matter: focus lift and sheen, layer
  slides, the end-card countdown ring, the LIVE pulse, the request-progress ring, and the channel-tune transition. No
  Lottie or Rive runtime is bundled.

## Mock data

Realistic UK household content, so the flows read true:
- **Up Next:** Loki S1 E4 (Disney+ and Jellyfin), The Simpsons S6 E12 (Jellyfin), Casper (half-watched, Jellyfin),
  Planet Earth III E3 (iPlayer), Severance (requested, downloading 62%).
- **Channels:** about 15 UK channels with real numbers (BBC One 101, BBC Two 102, ITV1 103, Channel 4 104, Channel 5
  105, ITV2 106, BBC Four 109, … Film4, BBC News) and a believable schedule spanning roughly −3 h to +24 h from the
  current clock time. Include The Simpsons on Channel 4 at 18:00 and EastEnders on BBC One at 19:00.
- **Watchlist / Discover:** a mix of playable and "get it" titles.
- Times are relative to the real current time, so NOW always looks right.

## What's left to the builder

- Exact layouts and spacing of the show page, search results and settings, within the direction above.
- Extra Home rows beyond the four named.
- Copywriting of minor labels; the remote-legend overlay (e.g. a small "?" key that shows the keyboard↔remote map).
- How artwork is gathered (TMDB CDN by known paths, or a one-off fetch script) and the fallback art style.
- Countdown lengths, fade timings, the channel-list tune delay, within the ranges stated.
- File structure inside `spikes/ux-redesign-prototype/` and how it's served (any simple static server bound to the
  Tailscale IP).

## How we'll judge it

The owner, using a keyboard as the remote, can: power on to Home → play Loki S1 E4 from Up Next → let it end (a fast-forwarded mock) → get offered S1 E5 → go
back Home → tune BBC One from On Now → flick channels with ↑↓ → open the Guide with G → find EastEnders → hold OK → Next
showing / Open the series → search "simpsons" and "severance" and see the play vs get difference. All of this without
focus ever being ambiguous, and it should look premium enough to sit beside Apple TV.

## Revision 2 (2026-10-26, after the owner tried the prototype)

All of these were proposed in chat and approved by the owner ("make all the proposed changes"):

1. **Focus:** lift to ~1.12, bright edge ring with soft glow, unfocused cards dimmed.
2. **Artwork sources and fallbacks:** Freesat/TvHeadend EPG has no images; programme art comes from matching titles against
   TMDB/TVDB. Fallback order everywhere: programme image → series image → branded channel card (logo on channel
   colour) → typographic card (title set large on a tint). Never grey. Some programmes in the mock are deliberately
   artless.
3. **No guide data for a channel:** On Now tile = branded channel card with "No programme information"; lit hero =
   channel name, Live, "No information for this channel", Watch only; guide = one long dimmed "No information" cell.
4. **Watchlist granularity:** a film, a series, a season or a single episode. Hold OK offers the right scope; cards
   show their scope ("Season 2", "S1 E4").
5. **Guide motion:** a highlight that slides between cells; the grid scrolls only near edges (~450 ms, gentle
   easing); the header crossfades; the ambient colour fades (~600 ms).
6. **All showings** (replaces "Next showing"): every showing of the programme across channels and 7 days, plus
   catch-up, marked New/Repeat; each can be jumped to or reminded. The mock schedule covers 7 days.
7. **"This channel's schedule"** is removed from guide actions; elsewhere it opens the guide focused on that channel.
8. **Columns experiment:** an alternative guide with channels as columns, time running down; ◂ ▸ channel, ▴ ▾ time,
   Pg± ±1 hour, digits jump to a channel. Switchable from the guide's strip and Settings.
9. **Remind me:** confirms what and when; a bell marks it in the guide; ~1 minute before, a countdown card offers
   Switch now / Dismiss and switches at the start unless dismissed. Never without warning.
10. **Tabs:** Home, Live TV, Search, Settings are a persistent tab bar. Moving across it switches the content
    immediately (no OK); ▾ enters the content; ▴ from the top of the content returns to the bar. Back from inside a
    place goes to its tab, then to Home. Show pages and the player still stack above.
11. **Search results** stay in their own column, a vertically scrolling grid, never sliding under the keyboard.
12. **Tuning transition:** the picture dims and a channel ident (number, name, what's on) shows a tuning indicator for
    ~1.5 s, then the new channel fades in.
13. **Channel list rows** show times and a to-scale timeline of now and next.
14. **Fixes:** every series has seasons and episodes; the Mac ⌫ delete icon; Settings values change with ◂ ▸; the
    channel list opens on the current channel then slides; ◂ ▸ scrub on the bare video (live and on-demand).
15. **Transitions and tell-tales:** shared-element transitions (card → show page; guide cell → actions; tile →
    video); a "‹ Back to …" chip showing where Back goes; one motion system (300–450 ms place changes, crossfades for
    content changes).

These supersede the earlier "◂ ▸ do nothing on the bare video", the stacked top-nav pages, and "Next showing on TV".
