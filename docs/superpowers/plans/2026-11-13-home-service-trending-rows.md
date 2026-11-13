# Home per-service trending rows Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Home shows Continue watching, On now, Your watchlist, Downloads, Discover, then Netflix / Disney+ / ITVX / Channel 4 / BBC iPlayer trending rows.

**Architecture:** Seerr's discover endpoint filtered by UK watch provider feeds a new `HomeData.ServiceTrendingAsync`; Seerr requests feed `HomeData.DownloadsAsync`; `HomeScreen.LoadRowsAsync` assembles the new row order. Empty rows are already dropped by `HomeScreen.Build`.

**Tech Stack:** C# / Avalonia, xUnit (`Baird.Tests`), Seerr REST (`api/v1/discover/...`).

**Spec:** `docs/superpowers/specs/2026-11-13-home-service-trending-rows-design.md`

## Global Constraints
- Row order and titles: `Continue watching`, `On now`, `Your watchlist`, `Downloads`, `Discover`, `Netflix`, `Disney+`, `ITVX`, `Channel 4`, `BBC iPlayer`.
- Region `GB`; TMDB provider ids Netflix 8, Disney+ 337, BBC iPlayer 38, ITVX 54, Channel 4 103 (verify against the real Seerr; constants live in one place).
- Every provider failure returns an empty list (never throws), logged like the other `HomeData` methods.
- No burst of requests on returning to Home (commit ce6ff50): service rows are cached in memory; Home reload must not refetch inside the lifetime.
- Don't deploy to or test on the Pi without messaging the other Baird agents first.

## Review Focus
- Seerr not configured (`_seerr == null`): Downloads and all service rows empty, Home still builds.
- Seerr discover items have **no `mediaType` field** (unlike trending); `ParseSearchResults` does `GetProperty("mediaType")` and would throw. The media type must come from the endpoint called.
- A service whose movie call fails but tv call succeeds still shows what it has.
- Returning to Home twice inside the cache lifetime makes one Seerr call set, not two.
- Downloads omits declined requests and requests already fully available.
- Existing nav tests that press Down N times to reach Discover keep working (Downloads empty → row omitted).

---

### Task 1: Seerr discover by watch provider

**Files:**
- Modify: `Baird/Services/IJellyseerrService.cs`, `Baird/Services/JellyseerrService.cs`
- Test: `Baird.Tests/Services/` (find the existing JellyseerrService test file with `grep -rl JellyseerrService Baird.Tests/Services`; add there, else new `JellyseerrDiscoverTests.cs`)

**Interfaces:**
- Produces: `Task<IEnumerable<JellyseerrSearchResult>> GetProviderTrendingAsync(int providerId, string mediaType, CancellationToken ct = default)` on `IJellyseerrService`, with a default body returning empty (so the many test fakes keep compiling). `mediaType` is `"movie"` or `"tv"`.
- Produces: `public static string JellyseerrService.DiscoverUrl(int providerId, string mediaType)` → `api/v1/discover/movies?watchProviders={id}&watchRegion=GB&sortBy=popularity.desc&page=1` (`tv` for `"tv"`).

- [ ] **Step 1: Failing tests**: `DiscoverUrl_movie_uses_movies_endpoint_with_gb_region_and_provider` (assert the exact string above for 8/"movie"; "tv" gives `discover/tv`); `Parse_discover_results_without_mediaType_uses_the_given_type` (JSON `{"results":[{"id":5,"title":"Alien","posterPath":"/p.jpg","releaseDate":"1979-05-25"}]}` for movie → one result with `MediaType=="movie"`, `Title=="Alien"`; for tv the title field is `name` and date `firstAirDate`, so also assert a tv JSON with `name`/`firstAirDate`). Parsing goes through an `internal static ParseDiscoverResults(string json, string mediaType)` (add `InternalsVisibleTo` only if not already present).
- [ ] **Step 2: Run** `dotnet test Baird.Tests --filter Discover` → FAIL (members missing).
- [ ] **Step 3: Implement** `DiscoverUrl`, `ParseDiscoverResults` (reuse the existing per-field mapping in `ParseSearchResults` for poster/overview/vote/`mediaInfo.status`; look at lines ~87-140 for how `MediaInfoStatus` is read), and `GetProviderTrendingAsync` modelled on `GetTrendingAsync` (same try/catch returning empty).
- [ ] **Step 4: Run** the same filter → PASS.
- [ ] **Step 5: Commit** `Seerr: discover popular titles by watch provider`.

### Task 2: HomeData service rows and Downloads

**Files:**
- Modify: `Baird/NewUi/Adapters/HomeData.cs`
- Test: `Baird.Tests/NewUi/HomeDataTests.cs` (extend its private `Seerr` fake with `GetProviderTrendingAsync` and settable requests)

**Interfaces:**
- Consumes: `IJellyseerrService.GetProviderTrendingAsync`, `GetRequestsAsync` (`JellyseerrRequest`: `Status` 1 pending/2 approved/3 declined, `MediaInfoStatus`, `DownloadProgress` 0-100 or null, `TmdbId`, `MediaType`, `PosterPath`, `Title`).
- Produces: `public static readonly IReadOnlyList<(string Title, int ProviderId)> ServiceRows` in `HomeData` (the five rows, in order, with the ids from Global Constraints); `Task<IReadOnlyList<HomeCard>> ServiceTrendingAsync(int providerId)`; `Task<IReadOnlyList<HomeCard>> DownloadsAsync()`; `public TimeSpan ServiceCacheLifetime { get; init; } = TimeSpan.FromMinutes(30)`.
- Cards use the same shape as `DiscoverAsync` (id `tmdb:{type}:{id}`, source `"Seerr"`, poster, year sublabel). Service-row cards carry no badge. Downloads cards carry `Badge = $"dl:{(int)progress}"` when `DownloadProgress` is known, else `"requested"`.

- [ ] **Step 1: Failing tests** in `HomeDataTests`: `ServiceTrending_merges_movies_and_tv_in_popularity_order_capped_at_20`; `ServiceTrending_keeps_tv_when_the_movie_call_throws`; `ServiceTrending_is_empty_without_seerr`; `ServiceTrending_second_call_inside_lifetime_does_not_call_seerr_again` (fake counts calls); `Downloads_lists_pending_and_downloading_with_a_percent_badge` (progress 40 → `"dl:40"`; no progress → `"requested"`); `Downloads_skips_declined_and_available_requests`.
- [ ] **Step 2: Run** `dotnet test Baird.Tests --filter HomeDataTests` → FAIL.
- [ ] **Step 3: Implement** the members above. Interleave movie and tv results (movie1, tv1, movie2, …) so one type doesn't dominate; cache per provider id with a timestamp (a `ConcurrentDictionary`); cache only non-empty results so a transient failure retries next Home load. Downloads is not cached (it must reflect progress).
- [ ] **Step 4: Run** the same filter → PASS.
- [ ] **Step 5: Commit** `Home data: per-service trending and Downloads`.

### Task 3: Home rows, order and rename

**Files:**
- Modify: `Baird/NewUi/Screens/HomeScreen.cs` (`LoadRowsAsync` ~line 93; `WithNowPlaying` line 118; line 279; `RefreshUpNextAsync` line 327; class summary comment line 21), `Baird/NewUi/Adapters/HomeData.cs` doc comments only if they name the row.
- Test: `Baird.Tests/NewUi/HomeScreenTests.cs`, and fix any test asserting the old title.

**Interfaces:**
- Consumes: Task 2's `ServiceRows`, `ServiceTrendingAsync`, `DownloadsAsync`.
- Produces: row title constant `"Continue watching"` (replace the four `"Up next"` row-title string uses in `HomeScreen.cs`, including the `"itemactions-upnext"` check and the eyebrow). Do **not** change the "Up next in …" end-card eyebrow, the "Remove from Up next" menu text, or toasts that say items appear in Up next — those are different things; leave and mention in the report.

- [ ] **Step 1: Failing tests** in `HomeScreenTests`: `Rows_are_in_the_agreed_order` (use the existing `Shown()` helper; extend its fake Seerr/Home so Downloads and at least two service rows have cards; assert `CurrentRows().Select(r => r.Title)` equals `Continue watching, On now, Your watchlist, Downloads, Discover, Netflix, Disney+` in that order); `A_service_row_with_no_cards_is_left_out`; `Now_playing_goes_first_in_Continue_watching`.
- [ ] **Step 2: Run** `dotnet test Baird.Tests --filter HomeScreenTests` → FAIL.
- [ ] **Step 3: Implement** `LoadRowsAsync`: start all tasks together (`upNext, onNow, watch, downloads, disc`, plus `ServiceRows.Select(r => ServiceTrendingAsync(r.ProviderId))`), `Task.WhenAll`, build rows in the order above; service rows are `CardShape.Poster` with eyebrow equal to the title. Apply the rename.
- [ ] **Step 4: Run** the full suite `dotnet test Baird.Tests` → all PASS (existing nav tests like `A_lit_poster_is_wholly_on_screen` rely on Downloads being empty in their fakes; if one now breaks, fix the fake, not the production order).
- [ ] **Step 5: Commit** `Home: Downloads and per-service trending rows, Continue watching`.

### Task 4: Verify against real Seerr

**Files:** none (verification; adjust Task 1/2 constants if the provider ids are wrong).

- [ ] **Step 1:** With the real Seerr URL/key from the app's config, `curl` `api/v1/discover/movies?watchProviders=8&watchRegion=GB&sortBy=popularity.desc` and check results are plausible Netflix UK titles; repeat for 337, 38, 54, 103 (and `discover/tv`). Report any provider returning nothing.
- [ ] **Step 2:** Do not deploy to the Pi. Tell the owner the change is ready and that a Pi check needs coordinating with the other agents (memory: coordinate-pi-tests).
