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

        private static HomeCard ItemCard(MediaItemViewModel m) =>
            new(m.Type == MediaType.Brand ? CardKind.Series : !string.IsNullOrWhiteSpace(m.Subtitle) ? CardKind.Episode : CardKind.Film,
                m.Id, m.Name,
                m.Type == MediaType.Brand ? m.Details : !string.IsNullOrWhiteSpace(m.Subtitle) ? m.Subtitle : m.Details,
                string.IsNullOrEmpty(m.ImageUrl) ? null : m.ImageUrl,
                m.History is { } h && !h.IsFinished && h.LastPosition > TimeSpan.Zero ? m.Progress : null,
                null, m.Source, m.StreamUrl is { Length: > 0 } ? ViaOf(m.Source) : PlayVia.None, m.StreamUrl, m.LastPosition, Synopsis: m.Synopsis);

        /// <summary>What you're part-way through (not live channels), most recent first.</summary>
        /// <summary>Items taken off Up next (id → when), so they stay off until watched again. Null = not remembered.</summary>
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
            catch (Exception ex) { Console.WriteLine($"[HomeData] Couldn't read hidden Up next items: {ex.Message}"); _hidden = new(); }
            return _hidden;
        }

        /// <summary>Takes an item off Up next (it comes back if it's watched again later).</summary>
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
            catch (Exception ex) { Console.WriteLine($"[HomeData] Couldn't save hidden Up next items: {ex.Message}"); }
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
        private readonly Dictionary<string, (DateTime When, HomeCard? Next)> _nextCache = new();

        public async Task<IReadOnlyList<HomeCard>> UpNextAsync()
        {
            try
            {
                var items = await _data.GetContinueWatchingAsync().ConfigureAwait(false);
                var cards = items.Where(i => !i.IsLive && i.Type != MediaType.Channel && !IsHidden(i.Id, i.History?.LastWatched ?? DateTime.MinValue))
                    .Select(i => (When: i.History?.LastWatched ?? DateTime.MinValue, Card: ItemCard(i))).ToList();

                // A finished episode leaves Up next, but the show's next episode (found through Seerr and the library) takes its place.
                if (FindNextAfter != null)
                {
                    foreach (var (when, card) in await NextEpisodesAsync(cards.Select(c => c.Card.Id).ToHashSet()).ConfigureAwait(false))
                        cards.Add((when, card));
                }
                return cards.OrderByDescending(c => c.When).Select(c => c.Card).ToList();
            }
            catch (Exception ex) { Console.WriteLine($"[HomeData] Up next failed: {ex.Message}"); return Array.Empty<HomeCard>(); }
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
                catch (Exception ex) when (ex is not OutOfMemoryException) { Console.WriteLine($"[HomeData] Next after {m.Name} failed: {ex.Message}"); return null; }
            })).ConfigureAwait(false);

            var result = new List<(DateTime, HomeCard)>();
            var seen = new HashSet<string>(already);
            for (var k = 0; k < finished.Count; k++)
                if (found[k] is { } card && seen.Add(card.Id)) result.Add((finished[k].History!.LastWatched, card));
            return result;
        }

        private static readonly TimeSpan NextCacheFor = TimeSpan.FromMinutes(5);

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

        /// <summary>The per-service rows on Home, in order, with each service's TMDB watch-provider id (UK).</summary>
        public static readonly IReadOnlyList<(string Title, int ProviderId)> ServiceRows = new[]
        {
            ("Netflix", 8), ("Disney+", 337), ("ITVX", 54), ("Channel 4", 103), ("BBC iPlayer", 38),
        };

        private const int ServiceRowCards = 20;
        /// <summary>How long a service's trending list is reused, so coming back to Home doesn't ask Seerr again.</summary>
        public TimeSpan ServiceCacheLifetime { get; init; } = TimeSpan.FromMinutes(30);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (DateTime At, IReadOnlyList<HomeCard> Cards)> _service = new();

        private static HomeCard SeerrCard(string type, int id, string title, string? releaseDate, string? poster, string? badge, string? overview) =>
            new(type == "tv" ? CardKind.Series : CardKind.Film, $"tmdb:{type}:{id}", title,
                releaseDate is { Length: >= 4 } d ? d[..4] : null,
                poster is { Length: > 0 } pp ? $"https://image.tmdb.org/t/p/w500{pp}" : null,
                null, badge, "Seerr", PlayVia.None, Synopsis: overview);

        /// <summary>What's popular on one streaming service in the UK (Seerr/TMDB): movies and series interleaved.</summary>
        public async Task<IReadOnlyList<HomeCard>> ServiceTrendingAsync(int providerId)
        {
            if (_seerr == null) return Array.Empty<HomeCard>();
            if (_service.TryGetValue(providerId, out var hit) && DateTime.UtcNow - hit.At < ServiceCacheLifetime) return hit.Cards;
            async Task<List<JellyseerrSearchResult>> Ask(string type)
            {
                try { return (await _seerr.GetProviderTrendingAsync(providerId, type).ConfigureAwait(false)).ToList(); }
                catch (Exception ex) { Console.WriteLine($"[HomeData] Trending {type} for provider {providerId} failed: {ex.Message}"); return new(); }
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
            IReadOnlyList<HomeCard> cards = merged.Take(ServiceRowCards)
                .Select(r => SeerrCard(r.MediaType, r.Id, r.Title, r.ReleaseDate, r.PosterPath, null, r.Overview)).ToList();
            if (cards.Count > 0) _service[providerId] = (DateTime.UtcNow, cards);
            return cards;
        }

        /// <summary>Seerr requests still on their way: waiting for approval, or downloading (with the percentage when known).</summary>
        public async Task<IReadOnlyList<HomeCard>> DownloadsAsync()
        {
            if (_seerr == null) return Array.Empty<HomeCard>();
            try
            {
                return (await _seerr.GetRequestsAsync().ConfigureAwait(false))
                    .Where(r => r.Status != 3 && !r.IsAvailable)
                    .Select(r => SeerrCard(r.MediaType, r.TmdbId, r.Title, null, r.PosterPath,
                        r.DownloadProgress is { } p ? $"dl:{(int)p}" : "requested", null))
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
