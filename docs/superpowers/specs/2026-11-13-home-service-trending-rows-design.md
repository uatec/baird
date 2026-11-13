# Home: per-service trending rows (short design)

## Intent
Home shows what's popular on each streaming service the owner has (Netflix, Disney+, ITVX, Channel 4, BBC iPlayer), so they can see what's worth watching without searching. Trending/featured, not personalised.

## Goals
- Row order: Continue watching, On now, Your watchlist, Downloads, Discover, Netflix, Disney+, ITVX, Channel 4, BBC iPlayer.
- Each service row lists that service's most popular UK titles (films and series, posters).
- Selecting a card opens the normal show page, which finds the title across sources (existing behaviour).

## Non-goals / anti-goals
- No personalised recommendations, no sign-in dependency, no scraping the services.
- Must not slow Home or cause request bursts (see home-return stall, ce6ff50). A failing or empty row is simply omitted/empty, like today's rows.

## Approaches
- A. Each service's own featured list: five separate integrations, Disney+ needs a signed-in browser. Rejected.
- B. Read signed-in browser home pages: slow, fragile, needs five logins. Rejected.
- **C. (chosen)** Seerr/TMDB discover filtered by UK watch provider (Netflix 8, Disney+ 337, iPlayer 38, ITVX 54, Channel 4 103 — builder to verify IDs and Seerr's `watchProviders`/`watchRegion` params). One code path, no logins, TMDB art. Trade-off: "popular on the service", not its curated featured shelf.

## Design
- `IJellyseerrService`: add a discover-by-provider call (movies + tv, popularity order, region GB), merged and trimmed to ~20 cards.
- `HomeData`: `ServiceTrendingAsync(provider)` returning `HomeCard`s the same shape as `DiscoverAsync` (id `tmdb:<type>:<id>`, Poster shape). Results cached in memory for a while (builder's call, e.g. 30 min) so returning to Home doesn't refetch.
- `HomeScreen.LoadRowsAsync`: new order; service rows load in parallel with the others; rows with no cards are skipped.
- Rename "Up next" to "Continue watching" everywhere it is a row title (including `WithNowPlaying`, the item-actions overlay key check, eyebrows).
- **Downloads row (my call, they haven't confirmed):** Seerr requests that are pending or downloading, shown with the existing `dl:<percent>` badge; omitted when empty.

## Constraints
- Seerr must be configured; without it the Discover, Downloads and service rows are empty (as Discover is today).
- Tests alongside existing `HomeDataTests`/`HomeScreenTests`.

## Left to the builder
Provider IDs and params, cache lifetime, card count, how the Downloads row maps request status, tests.

## Outcome notes (2026-11-13)
- `origin/main` already had a Downloads row (417635e) and the "Continue watching" rename (d808b08) by the time this was built, so this change only reorders Downloads to sit after the watchlist (the owner's order) and adds the five service rows. The plan's Task 2 `DownloadsAsync` and rename steps were dropped.
- Service rows load after Home's first build, like Downloads, and are cached in `HomeData` (30 min when complete; 2 min when empty or half-answered, so a Seerr outage doesn't cause 10 calls per Home visit).
- Provider ids (Netflix 8, Disney+ 337, ITVX 54, Channel 4 103, iPlayer 38) are unverified against a live Seerr.
