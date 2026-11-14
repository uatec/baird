using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baird.Models;
using Baird.NewUi.Model;
using Baird.Services;
using Baird.ViewModels;

namespace Baird.NewUi.Adapters
{
    /// <summary>
    /// Home's rows from the existing services. The only place (with <see cref="HistoryWriter"/>) that touches the
    /// legacy view-model types. Every row survives a failing provider: it returns what it has.
    /// </summary>
    public sealed class HomeData
    {
        private readonly IDataService _data;
        private readonly IEpgService _epg;
        private readonly IJellyseerrService? _seerr;
        private static readonly TimeSpan EpgTimeout = TimeSpan.FromSeconds(4);

        private readonly ProgrammeArt? _art;
        /// <summary>How long On now waits for pictures it has to look up (cached after the first time).</summary>
        public static readonly TimeSpan ArtBudget = TimeSpan.FromSeconds(3);

        public HomeData(IDataService data, IEpgService epg, IJellyseerrService? seerr, ProgrammeArt? art = null)
        {
            _data = data; _epg = epg; _seerr = seerr; _art = art;
        }

        /// <summary>Fills in a picture for programmes the guide has none for, within <see cref="ArtBudget"/>.</summary>
        public async Task<Programme?[]> WithArtAsync(Programme?[] programmes)
        {
            if (_art == null) return programmes;
            using var cts = new CancellationTokenSource(ArtBudget);
            var looked = programmes.Select(async p =>
            {
                if (p == null || !string.IsNullOrEmpty(p.ImageUrl)) return p;
                try { return await _art.ForAsync(p, cts.Token).ConfigureAwait(false) is { } url ? p with { ImageUrl = url } : p; }
                catch { return p; }
            });
            var all = Task.WhenAll(looked);
            var done = await Task.WhenAny(all, Task.Delay(ArtBudget + TimeSpan.FromMilliseconds(200))).ConfigureAwait(false);
            return done == all ? await all.ConfigureAwait(false) : programmes;
        }

        public static PlayVia ViaOf(string source) =>
            source.StartsWith("Netflix", StringComparison.OrdinalIgnoreCase) || source.StartsWith("Disney", StringComparison.OrdinalIgnoreCase)
            || source == ItvxProvider.SourceName || source == Channel4Provider.SourceName ? PlayVia.Browser : PlayVia.Mpv;

        /// <summary>"S4 E2 · The Griffin Incident" when the provider says which episode it is, else the episode's title.</summary>
        private static string EpisodeLine(MediaItemViewModel m) =>
            System.Text.RegularExpressions.Regex.IsMatch(m.Details ?? "", @"^S\d+ E\d+$") ? $"{m.Details} · {m.Subtitle}" : m.Subtitle;

        private static HomeCard ItemCard(MediaItemViewModel m) =>
            new(m.Type == MediaType.Brand ? CardKind.Series : !string.IsNullOrWhiteSpace(m.Subtitle) ? CardKind.Episode : CardKind.Film,
                m.Id, m.Name,
                m.Type == MediaType.Brand ? m.Details : !string.IsNullOrWhiteSpace(m.Subtitle) ? EpisodeLine(m) : m.Details,
                string.IsNullOrEmpty(m.ImageUrl) ? null : m.ImageUrl,
                m.History is { } h && !h.IsFinished && h.LastPosition > TimeSpan.Zero ? m.Progress : null,
                null, m.Source, m.StreamUrl is { Length: > 0 } ? ViaOf(m.Source) : PlayVia.None, m.StreamUrl, m.LastPosition, Synopsis: m.Synopsis);

        /// <summary>Items taken off Continue watching (id → when), so they stay off until watched again. Null = not remembered.</summary>
        public string? HiddenPath { get; init; }
        public static string DefaultHiddenPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".baird", "up-next-hidden.json");
        private Dictionary<string, string>? _hidden;

        private Dictionary<string, string> Hidden()
        {
            if (_hidden != null) return _hidden;
            try
            {
                _hidden = HiddenPath != null && System.IO.File.Exists(HiddenPath)
                    ? System.Text.Json.JsonSerializer.Deserialize(System.IO.File.ReadAllText(HiddenPath), BairdJsonContext.Default.DictionaryStringString) ?? new()
                    : new();
            }
            catch (Exception ex) { Console.WriteLine($"[HomeData] Couldn't read hidden Continue watching items: {ex.Message}"); _hidden = new(); }
            return _hidden;
        }

        /// <summary>Takes an item off Continue watching (it comes back if it's watched again later).</summary>
        public Task HideFromUpNextAsync(string id)
        {
            var hidden = Hidden();
            hidden[id] = DateTime.Now.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                if (HiddenPath != null)
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(HiddenPath)!);
                    System.IO.File.WriteAllText(HiddenPath, System.Text.Json.JsonSerializer.Serialize(hidden, BairdJsonContext.Default.DictionaryStringString));
                }
            }
            catch (Exception ex) { Console.WriteLine($"[HomeData] Couldn't save hidden Continue watching items: {ex.Message}"); }
            return Task.CompletedTask;
        }

        private bool IsHidden(string id, DateTime lastWatched) =>
            Hidden().TryGetValue(id, out var at) && DateTime.TryParse(at, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var when) && when >= lastWatched;

        /// <summary>
        /// What comes after a finished episode (or null): set by the shell, which knows how to find the show. See <see cref="ShowData.FollowingAsync"/>.
        /// </summary>
        public Func<MediaItemViewModel, CancellationToken, Task<HomeCard?>>? FindNextAfter { get; set; }
        /// <summary>How many of the most recently finished items are asked about, and for how long each may take.</summary>
        public const int MaxFinishedLookups = 6;
        public static readonly TimeSpan NextLookupTimeout = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan NextCacheFor = TimeSpan.FromMinutes(5);
        private readonly Dictionary<string, (DateTime When, HomeCard? Next)> _nextCache = new();

        /// <summary>
        /// Continue watching: what you've watched for a minute or more, most recent first — channels, and videos you haven't finished.
        /// A finished episode leaves it, but its show's next episode (found through the library and Seerr) takes its place.
        /// </summary>
        public async Task<IReadOnlyList<HomeCard>> UpNextAsync()
        {
            try
            {
                var items = (await _data.GetContinueWatchingAsync().ConfigureAwait(false))
                    .Where(i => !IsHidden(i.Id, i.History?.LastWatched ?? DateTime.MinValue))
                    .OrderByDescending(i => i.History?.LastWatched ?? DateTime.MinValue).ToList();
                var cards = await Task.WhenAll(items.Select(async i => i.IsLive || i.Type == MediaType.Channel ? await ChannelCardAsync(i).ConfigureAwait(false) : ItemCard(i))).ConfigureAwait(false);
                var list = items.Zip(cards, (m, c) => (When: m.History?.LastWatched ?? DateTime.MinValue, Card: c)).Where(x => x.Card != null).Select(x => (x.When, Card: x.Card!)).ToList();

                if (FindNextAfter != null)
                    list.AddRange(await NextEpisodesAsync(list.Select(x => x.Card.Id).ToHashSet()).ConfigureAwait(false));
                return list.OrderByDescending(x => x.When).Select(x => x.Card).ToList();
            }
            catch (Exception ex) { Console.WriteLine($"[HomeData] Continue watching failed: {ex.Message}"); return Array.Empty<HomeCard>(); }
        }

        private async Task<IReadOnlyList<(DateTime When, HomeCard Card)>> NextEpisodesAsync(HashSet<string> already)
        {
            IEnumerable<MediaItemViewModel> history;
            try { history = await _data.GetHistoryItemsAsync().ConfigureAwait(false); }
            catch (Exception ex) { Console.WriteLine($"[HomeData] Finished episodes failed: {ex.Message}"); return Array.Empty<(DateTime, HomeCard)>(); }

            var finished = history.Where(i => i.History is { IsFinished: true } && !i.IsLive && i.Type == MediaType.Video && !IsHidden(i.Id, i.History!.LastWatched))
                .OrderByDescending(i => i.History!.LastWatched).Take(MaxFinishedLookups).ToList();
            var found = await Task.WhenAll(finished.Select(async m =>
            {
                var key = $"{m.Id}@{m.History!.LastWatched.Ticks}";
                lock (_nextCache)
                    if (_nextCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.When < NextCacheFor) return hit.Next;
                try
                {
                    using var cts = new CancellationTokenSource(NextLookupTimeout);
                    var next = await FindNextAfter!(m, cts.Token).ConfigureAwait(false);
                    lock (_nextCache) _nextCache[key] = (DateTime.UtcNow, next);
                    return next;
                }
                catch (Exception ex) { Console.WriteLine($"[HomeData] Next after {m.Name} failed: {ex.Message}"); return null; }
            })).ConfigureAwait(false);

            var result = new List<(DateTime, HomeCard)>();
            var seen = new HashSet<string>(already);
            for (var k = 0; k < finished.Count; k++)
                if (found[k] is { } card && seen.Add(card.Id)) result.Add((finished[k].History!.LastWatched, card));
            return result;
        }

        /// <summary>A channel in Continue watching looks like its On now card (what's on now, no progress bar). Null if it can't be tuned any more.</summary>
        private async Task<HomeCard?> ChannelCardAsync(MediaItemViewModel m)
        {
            if (string.IsNullOrEmpty(m.StreamUrl)) return null;
            var channel = new Channel(m.Id, m.ChannelNumber ?? "", m.Name, string.IsNullOrEmpty(m.ImageUrl) ? null : m.ImageUrl, m.StreamUrl);
            var p = await CurrentProgrammeAsync(m.Id).ConfigureAwait(false);
            var label = string.IsNullOrEmpty(channel.Number) ? channel.Name : $"{channel.Number}  {channel.Name}";
            return new HomeCard(CardKind.Channel, m.Id, label, p?.Title, channel.LogoUrl, null, "live", "TV", PlayVia.Mpv, channel.StreamUrl, Channel: channel, Programme: p, Synopsis: p?.Synopsis);
        }

        /// <summary>Live channels in channel-number order, each with what's on now (when the guide answers in time).</summary>
        public async Task<IReadOnlyList<HomeCard>> OnNowAsync(DateTimeOffset now)
        {
            var channels = await ChannelsAsync().ConfigureAwait(false);
            var programmes = await WithArtAsync(await Task.WhenAll(channels.Select(c => CurrentProgrammeAsync(c.Id))).ConfigureAwait(false)).ConfigureAwait(false);
            return channels.Select((c, k) =>
            {
                var p = programmes[k];
                return new HomeCard(CardKind.Channel, c.Id, $"{c.Number}  {c.Name}", p?.Title, c.LogoUrl, p?.ProgressAt(now), "live",
                    "TV", PlayVia.Mpv, c.StreamUrl, Channel: c, Programme: p, Synopsis: p?.Synopsis);
            }).ToList();
        }

        /// <summary>Live channels that have a number (not 0) and a stream, in channel-number order (On now and the guide).</summary>
        public async Task<IReadOnlyList<Channel>> ChannelsAsync()
        {
            IEnumerable<MediaItemViewModel> listing;
            try { listing = await _data.GetListingAsync().ConfigureAwait(false); }
            catch (Exception ex) { Console.WriteLine($"[HomeData] Listing failed: {ex.Message}"); return Array.Empty<Channel>(); }

            return listing.Where(i => i.IsLive && !string.IsNullOrEmpty(i.ChannelNumber) && i.ChannelNumber != "0" && !string.IsNullOrEmpty(i.StreamUrl))
                .OrderBy(i => int.TryParse(i.ChannelNumber, out var n) ? n : int.MaxValue)
                .Select(i => new Channel(i.Id, i.ChannelNumber!, i.Name, string.IsNullOrEmpty(i.ImageUrl) ? null : i.ImageUrl, i.StreamUrl!))
                .ToList();
        }

        public async Task<Programme?> CurrentProgrammeAsync(string channelId)
        {
            try
            {
                using var cts = new CancellationTokenSource(EpgTimeout);
                var task = _epg.GetCurrentProgrammeAsync(channelId);
                var done = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cts.Token)).ConfigureAwait(false);
                return done == task ? Map(await task.ConfigureAwait(false)) : null;
            }
            catch (Exception ex) { Console.WriteLine($"[HomeData] Guide for {channelId} failed: {ex.Message}"); return null; }
        }

        /// <summary>The first programme starting after what's on now (for "Next: 18:30 Regional News").</summary>
        public async Task<Programme?> NextProgrammeAsync(string channelId, DateTimeOffset now)
        {
            try
            {
                using var cts = new CancellationTokenSource(EpgTimeout);
                var task = _epg.GetChannelScheduleAsync(channelId, 4);
                var done = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cts.Token)).ConfigureAwait(false);
                if (done != task) return null;
                return (await task.ConfigureAwait(false)).Select(Map).Where(p => p != null && p.Start > now).OrderBy(p => p!.Start).FirstOrDefault();
            }
            catch (Exception ex) { Console.WriteLine($"[HomeData] Next on {channelId} failed: {ex.Message}"); return null; }
        }

        public static Programme? Map(EpgProgram? p) => p == null || p.StartUnix <= 0 ? null :
            new Programme(p.Id, p.ChannelId, p.Title, string.IsNullOrWhiteSpace(p.Subtitle) ? null : p.Subtitle,
                string.IsNullOrWhiteSpace(p.DisplayDetails) ? null : p.DisplayDetails,
                DateTimeOffset.FromUnixTimeSeconds(p.StartUnix), DateTimeOffset.FromUnixTimeSeconds(p.StopUnix), ImageUrl: p.ImageUrl);

        public async Task<IReadOnlyList<HomeCard>> WatchlistAsync()
        {
            try { return (await _data.GetWatchlistItemsAsync().ConfigureAwait(false)).Select(ItemCard).ToList(); }
            catch (Exception ex) { Console.WriteLine($"[HomeData] Watchlist failed: {ex.Message}"); return Array.Empty<HomeCard>(); }
        }

        /// <summary>Seerr trending: titles you can request (or already have).</summary>
        public async Task<IReadOnlyList<HomeCard>> DiscoverAsync()
        {
            if (_seerr == null) return Array.Empty<HomeCard>();
            try
            {
                var results = await _seerr.GetTrendingAsync(1).ConfigureAwait(false);
                return results.Select(r => new HomeCard(r.MediaType == "tv" ? CardKind.Series : CardKind.Film, $"tmdb:{r.MediaType}:{r.Id}", r.Title,
                        r.ReleaseDate is { Length: >= 4 } d ? d[..4] : null,
                        r.PosterPath is { Length: > 0 } pp ? $"https://image.tmdb.org/t/p/w500{pp}" : null,
                        null, r.IsAvailable ? null : "get", "Seerr", PlayVia.None, Synopsis: r.Overview))
                    .ToList();
            }
            catch (Exception ex) { Console.WriteLine($"[HomeData] Discover failed: {ex.Message}"); return Array.Empty<HomeCard>(); }
        }

        /// <summary>The per-service rows on Home, in order, with each service's TMDB watch-provider id (UK; checked against Seerr's own provider list).</summary>
        public static readonly IReadOnlyList<(string Title, int ProviderId)> ServiceRows = new[]
        {
            ("Netflix", 8), ("Disney+", 337), ("ITVX", 41), ("Channel 4", 103), ("BBC iPlayer", 38),
        };

        private const int ServiceRowCards = 20;
        /// <summary>How long a complete trending list is reused, so coming back to Home doesn't ask Seerr again.</summary>
        public TimeSpan ServiceCacheLifetime { get; init; } = TimeSpan.FromMinutes(30);
        /// <summary>How long an empty or half answer (movies or series missing) is reused before Seerr is asked again.</summary>
        public TimeSpan ServiceRetryLifetime { get; init; } = TimeSpan.FromMinutes(2);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (DateTime At, TimeSpan Keep, IReadOnlyList<HomeCard> Cards)> _service = new();

        /// <summary>What's popular on one streaming service in the UK (Seerr/TMDB): movies and series interleaved.</summary>
        public async Task<IReadOnlyList<HomeCard>> ServiceTrendingAsync(int providerId)
        {
            if (_seerr == null) return Array.Empty<HomeCard>();
            if (_service.TryGetValue(providerId, out var hit) && DateTime.UtcNow - hit.At < hit.Keep) return hit.Cards;
            var failed = false;
            async Task<List<JellyseerrSearchResult>> Ask(string type)
            {
                try { return (await _seerr.GetProviderTrendingAsync(providerId, type).ConfigureAwait(false)).ToList(); }
                catch (Exception ex) { failed = true; Console.WriteLine($"[HomeData] Trending {type} for provider {providerId} failed: {ex.Message}"); return new(); }
            }
            var movies = Ask("movie");
            var tv = Ask("tv");
            await Task.WhenAll(movies, tv).ConfigureAwait(false);
            var m = movies.Result; var t = tv.Result;
            var merged = new List<JellyseerrSearchResult>();
            for (var i = 0; i < Math.Max(m.Count, t.Count) && merged.Count < ServiceRowCards; i++)
            {
                if (i < m.Count) merged.Add(m[i]);
                if (i < t.Count && merged.Count < ServiceRowCards) merged.Add(t[i]);
            }
            IReadOnlyList<HomeCard> cards = merged.Select(r => new HomeCard(r.MediaType == "tv" ? CardKind.Series : CardKind.Film, $"tmdb:{r.MediaType}:{r.Id}", r.Title,
                    r.ReleaseDate is { Length: >= 4 } d ? d[..4] : null,
                    r.PosterPath is { Length: > 0 } pp ? $"https://image.tmdb.org/t/p/w500{pp}" : null,
                    null, null, "Seerr", PlayVia.None, Synopsis: r.Overview)).ToList();
            // Only a full answer (movies and series, no errors) is kept for long; an empty or half answer is retried soon.
            var complete = !failed && m.Count > 0 && t.Count > 0;
            _service[providerId] = (DateTime.UtcNow, complete ? ServiceCacheLifetime : ServiceRetryLifetime, cards);
            return cards;
        }
        /// <summary>A Downloads tile's two messages: <see cref="Badge"/> is the state (the pill), <see cref="Scope"/> says which part.</summary>
        public sealed record DownloadView(string Badge, double? Progress, string Scope);

        /// <summary>A request nobody has found anything for after this long says "Not found" instead of "Searching".</summary>
        public static readonly TimeSpan NotFoundAfter = TimeSpan.FromDays(3);

        private static DateTime When(string s) => DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : DateTime.MinValue;

        /// <summary>"S1–3", "S1, S3–4": season numbers as runs.</summary>
        public static string Runs(IEnumerable<int> numbers)
        {
            var parts = new List<string>();
            int? from = null, to = null;
            void Flush() { if (from != null) parts.Add(from == to ? $"S{from}" : $"S{from}–{to}"); }
            foreach (var n in numbers.Distinct().OrderBy(n => n))
            {
                if (from != null && n == to + 1) { to = n; continue; }
                Flush();
                from = to = n;
            }
            Flush();
            return string.Join(", ", parts);
        }

        /// <summary>The seasons asked for across all of a title's requests (a series can be requested a season at a time).</summary>
        public static Dictionary<(string Type, int Tmdb), IReadOnlyList<int>> RequestedSeasonsByTitle(IEnumerable<JellyseerrRequest> requests) =>
            requests.Where(r => r.MediaType == "tv").GroupBy(r => (r.MediaType, r.TmdbId))
                .ToDictionary(g => g.Key, g => (IReadOnlyList<int>)g.SelectMany(r => r.RequestedSeasons).Distinct().OrderBy(n => n).ToList());

        /// <summary>
        /// Where a request has got to. The pill: "requested" (awaiting approval), "searching", "out:18 Dec" (not released),
        /// "dl:42" (downloading, with a progress bar), "part" (some of what was asked for is here), "ready" (everything that was
        /// asked for is here, but not the whole show), "complete" (the whole show), "notfound" (nothing found for days) or
        /// "declined". The scope line says which seasons, and how many episodes are still to come.
        /// </summary>
        /// <param name="requested">The seasons asked for across the title's requests; empty when Seerr doesn't say (then the seasons it knows of stand in).</param>
        public static DownloadView Describe(JellyseerrRequest r, IReadOnlyCollection<int> requested, DateTime now)
        {
            var askedAt = When(r.CreatedAt);
            var asked = askedAt > DateTime.MinValue ? askedAt.ToLocalTime().ToString("d MMM") : null;
            var overall = r.DownloadProgress;
            var progress = overall / 100;
            string? notOut = DateTime.TryParse(r.ReleaseDate, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var rel) && rel.Date > now.Date
                ? $"out:{rel:d MMM}{(rel.Year != now.Year ? $" {rel.Year}" : "")}" : null;
            // Approved and being looked for (Seerr's media "processing" or further on), as against still awaiting approval.
            var looking = r.Status != 1 && r.MediaInfoStatus >= 3;
            var waitedLong = askedAt > DateTime.MinValue && now - askedAt >= NotFoundAfter;

            if (r.MediaType != "tv")
            {
                if (r.Status == 3) return new("declined", null, "Declined");
                if (r.MediaInfoStatus == 5) return new("ready", null, "Film");
                if (overall is { } pc) return new($"dl:{pc:0}", pc / 100, "Downloading");
                if (!looking) return new("requested", null, asked == null ? "Requested" : $"Asked {asked}");
                if (notOut != null) return new(notOut, null, "Not released yet");
                return waitedLong ? new("notfound", null, "Nothing found yet") : new("searching", null, "Looking for it");
            }

            var status = new Dictionary<int, int>();
            foreach (var s in r.Seasons) status[s.Number] = s.Status;
            int Of(int n) => status.TryGetValue(n, out var st) ? st : 1;
            var all = r.Seasons.Where(s => s.Number > 0).Select(s => s.Number).ToList();
            // A season deleted (7) or blocklisted (6) is neither here nor on the way, so it is no longer part of what was asked for.
            var asking = (requested.Count > 0 ? requested.Where(n => n > 0) : all.Where(n => Of(n) >= 2)).Where(n => Of(n) is not (6 or 7)).Distinct().OrderBy(n => n).ToList();
            var scope = asking.Count > 0 ? Runs(asking) : "";
            string With(string tail) => scope.Length == 0 ? tail : $"{scope} · {tail}";

            if (r.Status == 3) return new("declined", null, With("declined"));
            if (r.MediaInfoStatus == 5 || (all.Count > 0 && all.All(n => Of(n) == 5)))
                return new("complete", null, all.Count > 1 ? $"All {all.Count} seasons" : "The whole series");
            var ready = asking.Where(n => Of(n) == 5).ToList();
            var onTheWay = asking.Where(n => Of(n) != 5).ToList();
            if (asking.Count > 0 && onTheWay.Count == 0)
                return new("ready", null, all.Count > asking.Count ? $"{scope} of {all.Count} seasons" : scope);

            int Coming(IEnumerable<int> seasons) => r.Downloads.Where(d => d.Season is { } s && seasons.Contains(s)).Select(d => (d.Season, d.Episode)).Distinct().Count();
            string ToGo(int episodes) => episodes == 1 ? "1 episode to go" : $"{episodes} episodes to go";

            if (ready.Count > 0 || asking.Any(n => Of(n) == 4))
            {
                var eps = Coming(onTheWay);
                // Short words: the line has one row of a poster tile ("S1 ready · S2 6 to go" fits, "...6 episodes to go" doesn't).
                var rest = $"{Runs(onTheWay)} {(eps > 0 ? $"{eps} to go" : onTheWay.All(n => Of(n) == 4) ? "partly" : "coming")}";
                return new("part", progress, ready.Count > 0 ? $"{Runs(ready)} ready · {rest}" : rest);
            }
            if (overall is { } p)
            {
                var eps = Coming(asking);
                return new($"dl:{p:0}", p / 100, eps > 0 ? With(ToGo(eps)) : scope);
            }
            if (!looking) return new("requested", null, asked == null ? scope : With($"asked {asked}"));
            if (notOut != null) return new(notOut, null, With("not out yet"));
            return waitedLong ? new("notfound", null, With("not found yet")) : new("searching", null, With("looking for it"));
        }

        /// <summary>How long a request that has arrived stays in Downloads.</summary>
        public static readonly TimeSpan KeepArrivedFor = TimeSpan.FromDays(14);

        /// <summary>
        /// Downloads: what's been asked of Seerr, newest request first, one card per title, each saying where it's got to
        /// (requested, downloading n%, partly here, ready). Arrived titles drop off after <see cref="KeepArrivedFor"/>.
        /// </summary>
        public async Task<IReadOnlyList<HomeCard>> DownloadsAsync()
        {
            if (_seerr == null) return Array.Empty<HomeCard>();
            try
            {
                var cutoff = DateTime.UtcNow - KeepArrivedFor;
                var requests = (await _seerr.GetRequestsAsync().ConfigureAwait(false)).ToList();
                var asked = RequestedSeasonsByTitle(requests);
                return requests
                    .Where(r => r.MediaInfoStatus is not (6 or 7)) // deleted from the server, or blocklisted: nothing left to follow
                    .Where(r => r.MediaInfoStatus != 5 || When(r.UpdatedAt) >= cutoff)
                    .OrderByDescending(r => When(r.CreatedAt))
                    .DistinctBy(r => (r.MediaType, r.TmdbId))
                    .Select(r =>
                    {
                        var tv = r.MediaType == "tv";
                        var view = Describe(r, asked.TryGetValue((r.MediaType, r.TmdbId), out var seasons) ? seasons : Array.Empty<int>(), DateTime.Now);
                        var poster = r.PosterPath is { Length: > 0 } pp ? $"https://image.tmdb.org/t/p/w500{pp}" : null;
                        var kind = tv ? TitleKind.Series : TitleKind.Film;
                        var title = new CatalogueTitle($"tmdb:{r.MediaType}:{r.TmdbId}", kind, r.Title, CatalogueSearch.YearOf(r.ReleaseDate), poster, null,
                            Array.Empty<TitleSource>(), new SeerrRef(r.TmdbId, r.MediaType, r.MediaInfoStatus == 0 ? 1 : r.MediaInfoStatus));
                        return new HomeCard(tv ? CardKind.Series : CardKind.Film, $"tmdb:{r.MediaType}:{r.TmdbId}", r.Title, view.Scope, poster,
                            view.Progress, view.Badge, "Seerr", PlayVia.None, Title: title);
                    })
                    .ToList();
            }
            catch (Exception ex) { Console.WriteLine($"[HomeData] Downloads failed: {ex.Message}"); return Array.Empty<HomeCard>(); }
        }
    }


    /// <summary>Saves watch progress through the legacy history service (which wants a MediaItemViewModel).</summary>
    public sealed class HistoryWriter
    {
        private readonly IDataService _data;
        public HistoryWriter(IDataService data) => _data = data;

        public async Task SaveAsync(string itemId, TimeSpan position, TimeSpan duration)
        {
            try
            {
                var item = await _data.GetItemAsync(itemId).ConfigureAwait(false);
                if (item == null) { Console.WriteLine($"[HistoryWriter] Unknown item {itemId}; progress not saved."); return; }
                await _data.UpsertHistoryAsync(item, position, duration).ConfigureAwait(false);
            }
            catch (Exception ex) { Console.WriteLine($"[HistoryWriter] Save failed for {itemId}: {ex.Message}"); }
        }
    }
}
