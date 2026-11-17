# Show-page data layer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show pages never hang, open fully populated from a persistent serve-stale cache, and everything directly reachable from a page is warmed through a prioritised queue that leaves video playback alone.

**Architecture:** Stage 1 bounds every network call and adds the cold-load spinner/error rule. Stage 2 adds a two-lifetime cache (availability 60s in memory, metadata 7 days on disk, both served stale) plus a single-worker refresh queue. Stage 3 replaces the lit-card `Prefetcher` with one-hop warming submitted to that queue. Each stage is mergeable on its own.

**Tech Stack:** C# / .NET, Avalonia (headless xUnit tests in `Baird.Tests/NewUi`), `System.Text.Json`. Run tests with `dotnet test Baird.Tests --filter <Name>`.

**Spec:** `docs/superpowers/specs/2026-11-16-show-page-hang-and-preload-design.md`

## Global Constraints
- Per-piece network timeout **8s**; cold page: spinner, fully populated target **3s**, error with retry at **8s**; never an error when stale data exists.
- Availability (library sources, Seerr status, download progress): cache **60s**, **in memory only**, served stale with **no upper age limit**. Watched/resume state is local history, always live.
- Metadata and images: **7 days**, served stale, refreshed by priority; a failed refresh never replaces, evicts or blanks old data.
- Disk metadata cache: `~/.baird/show_cache/`, **one file per title**, versioned (unknown version = ignored), LRU at **5,000** titles, saves debounced about **30s**. Image disk cache about **1 GB**, oldest first.
- Never persist secrets (tokenize via `SecretRedactor` as `JsonMediaDataCache` does) or stream URLs. Never warm stream resolution (yt-dlp).
- Unknown availability shows a neutral **"–"** with no wording, on season state labels and Request/Play button text only. Remove the "Checking your library…" text.
- Queue: one worker, one job at a time, bounded **200**, deduplicated by key, newer batch first; order within a batch: current page, children, siblings. Warming is **one hop** only.
- While video plays: one job per **1-2s**; paused entirely on dropped frames; stopped when Home is closed during full-screen playback.
- Cards that carry a source can Play at once; Seerr-only cards wait on "–".

## Review Focus
1. A provider whose `GetChildrenAsync` never returns: page still completes within the budget (Task 1).
2. A truncated, corrupt or wrong-version cache file: ignored, never crashes startup or a page (Task 5).
3. A Jellyfin `api_key` inside a cached image URL: never written to disk (Task 5).
4. A background refresh that fails or times out while stale data is on screen: old data stays, no error (Task 7).
5. Home with hundreds of cards: the queue stays at 200, deduplicates, runs the newest batch first, and slows while video plays (Task 8).

---

## Stage 1: no hangs, honest loading

### Task 1: Bound every availability call
**Files:** Modify `Baird/NewUi/Adapters/ShowData.cs` (`StatusAsync`, `SeasonsFromAsync`, constructor); Modify `Baird/NewUi/Adapters/CatalogueSearch.cs` (reuse); Test `Baird.Tests/NewUi/ShowDataTimeoutTests.cs`.
**Interfaces:** Produces `ShowData(IDataService data, IJellyseerrService? seerr, CatalogueSearch search, TimeSpan? pieceTimeout = null)` (default 8s); `ShowData.Budget` (`TimeSpan`, 8s, settable via ctor `TimeSpan? budget = null` for tests).
- [ ] **Step 1: Failing tests** in `ShowDataTimeoutTests`: `A_hung_season_listing_does_not_hold_the_page` (fake library whose `GetChildrenAsync` never completes, piece timeout 200ms: `DetailsAsync` for The Simpsons completes within 2s with no seasons from that source); `A_hung_seerr_status_leaves_the_library_seasons` (hung `GetMediaStatusAsync`: library seasons still returned).
- [ ] **Step 2:** Run `dotnet test Baird.Tests --filter ShowDataTimeoutTests`; expect FAIL (timeouts).
- [ ] **Step 3:** Implement a shared `static Task<T> Within<T>(Func<CancellationToken, Task<T>> call, T fallback, TimeSpan timeout, string what, CancellationToken ct)` in a new `Baird/NewUi/Adapters/Deadline.cs`; make `CatalogueSearch.Within` call it; wrap `StatusAsync` and every `GetChildrenAsync` in `SeasonsFromAsync` (hung = empty/null, logged like existing failures).
- [ ] **Step 4:** Run the filter; expect PASS. Run `dotnet test Baird.Tests`; expect no regressions.
- [ ] **Step 5:** Commit `fix: bound show-page availability calls`.

### Task 2: "–" replaces "Checking your library…"
**Files:** Modify `Baird/NewUi/Model/Catalogue.cs:88` (`Checking = "–"`), `Baird/NewUi/Screens/ShowPage.cs:217-218` (button "Loading…" -> "–"); update tests that assert the old text (`grep -rn "Checking your library" Baird.Tests`).
- [ ] **Step 1:** Change the assertions in existing tests to `"–"`; add `Unknown_availability_reads_as_a_dash_with_no_words` in `SearchAndShowTests` (a Seerr-only title's `Summary` equals `"–"`).
- [ ] **Step 2:** Run those tests; expect FAIL. **Step 3:** Make the changes above (the search screen text in `CatalogueSearch.cs:248` doc comment too). **Step 4:** Run `dotnet test Baird.Tests`; expect PASS. **Step 5:** Commit.

### Task 3: Cold spinner, 3s/8s, error with retry
**Files:** Modify `ShowData.cs` (`DetailsAsync`), `Baird/NewUi/Screens/ShowPage.cs` (`LoadAsync`, `RenderBody`/`RenderLower`); Test `Baird.Tests/NewUi/ShowDataTimeoutTests.cs`, `SearchAndShowTests.cs`.
**Interfaces:** Produces `class ShowLoadTimeoutException : Exception` thrown by `DetailsAsync` only when the budget passes with no progress ever reported and no cached data; when progress exists it returns the latest details with `CheckingLibrary = false`.
- [ ] **Step 1: Failing tests:** `Nothing_by_the_budget_is_an_error` (all providers and Seerr hung, budget 200ms: throws `ShowLoadTimeoutException`); `Something_by_the_budget_is_returned_not_an_error` (Seerr answers, library hung: returns Seerr's view, `CheckingLibrary == false`); `The_page_shows_a_spinner_then_an_error_with_retry` (ShowPage with hung data and `FakeTimers`: spinner visible at 0s; after 8s the error state shows and OK retries via `LoadAsync(fresh: true)`).
- [ ] **Step 2:** Run; expect FAIL. **Step 3:** In `DetailsAsync` race `e.Task` against `Task.Delay(Budget)` (do not cancel the entry on budget; a retry joins it). In `ShowPage` show the repo's `Baird/Assets/Lottie/loading-ring.json` (follow `LottieSpikeControl` for the control) over the card's own title/art/synopsis while `_details == null`; on `ShowLoadTimeoutException` set a failed state ("Couldn't load this page. Press OK to try again.") handled in the OK key path. **Step 4:** PASS. **Step 5:** Commit.
- [ ] **Step 6 (Stage 1 gate):** deploy to the Pi, open a Seerr-only title and a library series, confirm on the physical screen (do not trust counters alone) that no page waits beyond 8s; record timings.

## Stage 2: cache and refresh queue

### Task 4: Seerr season details
**Files:** Modify `Baird/Services/IJellyseerrService.cs` (add `Task<JellyseerrSeasonDetails?> GetSeasonAsync(int tmdbId, int season, CancellationToken ct = default) => Task.FromResult<JellyseerrSeasonDetails?>(null)` and `record JellyseerrSeasonDetails(int Number, IReadOnlyList<JellyseerrEpisode> Episodes)`, `record JellyseerrEpisode(int Number, string Name, string? Overview, string? StillUrl)`), `Baird/Services/JellyseerrService.cs` (`GET api/v1/tv/{id}/season/{n}`, public static `ParseSeason(string json)`, still URL `https://image.tmdb.org/t/p/w500{stillPath}`), `ShowData.Merge` (use details for episodes not in the library), `Baird.Tests/NewUi/CatalogueTests.cs` `FakeSeerr` (a `Seasons` dictionary); Test `Baird.Tests/Services/JellyseerrSeasonTests.cs`.
- [ ] **Step 1:** Tests `Season_details_parse_names_stills_and_synopses` (sample JSON) and `A_seerr_only_series_lists_real_episodes` (Severance: episode label `"1. Good News About Hell"`, still and synopsis set, not `"1. Episode 1"`). **Step 2:** FAIL. **Step 3:** Implement; `ComputeAsync` fetches the season that will open first (Task 7's rule) with the piece timeout; `Merge` takes an optional `IReadOnlyDictionary<int, JellyseerrSeasonDetails>`. **Step 4:** PASS. **Step 5:** Commit.

### Task 5: Persistent metadata store
**Files:** Create `Baird/NewUi/Adapters/ShowStore.cs`; Test `Baird.Tests/NewUi/ShowStoreTests.cs`.
**Interfaces:** Produces `interface IShowStore { bool TryGet(string key, out StoredShow? show); void Put(string key, StoredShow show); }`, `record StoredShow(CatalogueTitle Title, IReadOnlyList<StoredSeason> Seasons, DateTimeOffset Stamp)`, `record StoredSeason(int Number, string Label, IReadOnlyList<StoredEpisode> Episodes, int? EpisodeCount)`, `record StoredEpisode(int Number, string Label, string? Sublabel, string? ImageUrl, string? Synopsis)`; `class DiskShowStore(string folder, ITimers timers, int maxTitles = 5000) : IShowStore`. Title sources/stream URLs are dropped on write; availability is never stored.
- [ ] **Step 1: Tests:** `A_stored_title_survives_a_restart`; `Image_urls_never_reach_disk_with_a_token` (a URL with `api_key=abc` is written tokenized and expanded on read); `A_corrupt_or_unknown_version_file_is_ignored`; `Saves_are_debounced_to_30_seconds_and_write_only_changed_titles` (FakeTimers); `The_5001st_title_drops_the_least_recently_used`. **Step 2:** FAIL. **Step 3:** Implement (file name = hash of key; version field; follow `JsonMediaDataCache` for tokenize/expand and `SecretRedactor`); register in the shell where `ShowData` is created (`NewShellView.cs`). **Step 4:** PASS. **Step 5:** Commit.

### Task 6: Availability cache (60s, in memory, stale served)
**Files:** Create `Baird/NewUi/Adapters/StaleCache.cs`; Modify `ShowData.cs`; Test `Baird.Tests/NewUi/StaleCacheTests.cs`.
**Interfaces:** Produces `class StaleCache<T>(TimeSpan fresh, Func<DateTimeOffset> now)` with `bool TryGet(string key, out T value, out bool stale)` and `void Put(string key, T value)`; entries never expire (age only sets `stale`). Replaces `ShowData._cache`'s 90s `Keep` for availability and `Forget()` on history updates keeps working.
- [ ] **Step 1: Tests:** `Fresh_then_stale_after_60_seconds_and_still_served`; `Put_replaces`. **Step 2:** FAIL. **Step 3:** Implement and use it for Seerr status and per-source library seasons inside `ComputeAsync`. **Step 4:** PASS; run full suite. **Step 5:** Commit.

### Task 7: ShowData serves stored metadata first, refreshes behind
**Files:** Modify `ShowData.cs` (`DetailsAsync`, `ComputeAsync`, constructor takes `IShowStore?`), `Baird/NewUi/Prefetcher.cs` (`OpeningSeason` stays); Test `Baird.Tests/NewUi/ShowDataCacheTests.cs`.
- [ ] **Step 1: Tests:** `A_stored_title_is_reported_before_any_network_answers` (progress callback fires synchronously-first with full episode names and `CheckingLibrary == true`); `A_stored_title_older_than_7_days_is_still_served_and_refresh_is_requested`; `A_failed_or_timed_out_refresh_keeps_the_old_record_and_raises_no_error` (Review Focus 4); `Live_results_rewrite_the_store`; `Play_waits_for_availability_except_for_cards_carrying_a_source`. **Step 2:** FAIL. **Step 3:** In `DetailsAsync` build `ShowDetails` from `StoredShow` (sources empty, "–" availability), report it first, then merge live availability and `Put` the result; a stale or missing record queues a refresh through the `WorkQueue` (Task 8 interface; use an `Action<string key, Func<CancellationToken, Task> work>` seam until then). **Step 4:** PASS. **Step 5:** Commit.

### Task 8: The prioritised work queue
**Files:** Create `Baird/NewUi/Adapters/WorkQueue.cs`; Test `Baird.Tests/NewUi/WorkQueueTests.cs`.
**Interfaces:** Produces `enum WarmMode { Run, Slow, Paused }`, `interface IWarmGate { WarmMode Mode { get; } }`, `enum Rank { Page = 0, Child = 1, Sibling = 2 }`, `record WorkItem(string Key, Rank Rank, Func<CancellationToken, Task> Run)`, `class WorkQueue(ITimers timers, IWarmGate gate, int capacity = 200)` with `void Submit(IReadOnlyList<WorkItem> batch)`, `void Urgent(WorkItem item)`, `Task Idle` (tests await). One worker; gap between jobs 1.5s when `Slow`, none when `Run`, stops when `Paused`.
- [ ] **Step 1: Tests:** `A_newer_batch_runs_before_an_older_one`; `Within_a_batch_page_then_children_then_siblings`; `Urgent_jumps_the_queue`; `Duplicate_keys_merge_keeping_the_better_rank_and_newer_batch`; `The_201st_oldest_item_is_dropped`; `Slow_mode_waits_between_jobs_and_Paused_runs_nothing`; `One_job_runs_at_a_time`. **Step 2:** FAIL. **Step 3:** Implement. **Step 4:** PASS. **Step 5:** Commit.

### Task 9: The warm gate and image disk cache
**Files:** Create `Baird/NewUi/Playback/WarmGate.cs` (`class WarmGate : IWarmGate` from the shell's `IsPlaying` at `NewShellView.cs:485` and the player's dropped-frame count; find the dropped-frames signal in the mpv player, as used by the Pi A/V tuning work); Modify `Baird/NewUi/Controls/ImageCache.cs` and the image loader setup in `App.axaml.cs` for a ~1 GB disk cache (check whether `AsyncImageLoader` offers `DiskCachedWebImageLoader`; otherwise add a small file cache keyed by URL hash with oldest-first eviction); wire `Refresh` work from Task 7 through the queue; Test `Baird.Tests/NewUi/WarmGateTests.cs`.
- [ ] **Step 1: Tests:** `Playing_is_slow_and_dropped_frames_pause`, `Home_closed_during_fullscreen_playback_pauses`, `An_image_warmed_before_a_restart_loads_from_disk`. **Step 2-4:** fail, implement, pass. **Step 5:** Commit.
- [ ] **Step 6 (Stage 2 gate):** on the Pi confirm a title opened twice, with a restart between, populates fully with the network off (Seerr unreachable), and play a video while Home warms: no new dropped frames. Verify on screen.

## Stage 3: one-hop warming

### Task 10: Warm what a page links to
**Files:** Create `Baird/NewUi/Warmer.cs` replacing `Prefetcher.Lit` usage (`HomeScreen.cs:209`, `ShowPage.cs:429`, `NewShellView.cs:100`, `NewUiContext.cs:76`); Modify `HomeScreen.cs`, `ShowPage.cs`, `SearchScreen.cs`; Test `Baird.Tests/NewUi/WarmerTests.cs` (adapt `PrefetchTests`).
**Interfaces:** Produces `class Warmer(WorkQueue queue, ShowData shows, ITimers timers)` with `void PageOpened(string pageKey, IReadOnlyList<CatalogueTitle> children, IReadOnlyList<CatalogueTitle> siblings)` which submits one batch (page's own data at `Rank.Page`, children `Rank.Child`, siblings `Rank.Sibling`) and never recurses. Each item refreshes metadata only; stream resolution is never submitted.
- [ ] **Step 1: Tests:** `Home_warms_continue_up_next_downloads_then_trending_in_that_order_and_skips_channels`; `A_show_page_warms_the_next_episode_season_first_then_nearest_outward` (partly watched Simpsons: season 2, then 1 and 3); `Only_one_hop_is_warmed`; `Going_back_warms_forward_pages_before_pages_behind` (newer batch first); `Lighting_an_unloaded_season_tab_is_urgent`; `No_stream_resolution_is_ever_queued`. **Step 2:** FAIL. **Step 3:** Implement; Home calls `PageOpened` once it settles (about 1s, `Timers.After`) with its page-opening cards in row order (use `Prefetcher.OpensAPage` logic), siblings = the row's other cards; delete `Prefetcher` once nothing uses it, keeping `OpeningSeason`/`UrlsFor` as static helpers on `Warmer`. **Step 4:** PASS; full suite. **Step 5:** Commit.
- [ ] **Step 6 (Stage 3 gate, on the Pi):** time ready-to-fully-populated before and after for a warmed series, a cold series and a Seerr-only title; check against Success numbers; confirm on screen with the viewer, not counters alone; confirm no new dropped frames while warming under live TV. Report the numbers.

## Success (from the spec)
Warm titles fully populated within 0.5s; cold within 3s, error at 8s; nothing waits on a hung provider beyond 8s; no new dropped frames while warming.
