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

        /// <summary>Continue watching: what you've watched for a minute or more, most recent first — channels, and videos you haven't finished.</summary>
        public async Task<IReadOnlyList<HomeCard>> UpNextAsync()
        {
            try
            {
                var items = (await _data.GetContinueWatchingAsync().ConfigureAwait(false))
                    .Where(i => !IsHidden(i.Id, i.History?.LastWatched ?? DateTime.MinValue))
                    .OrderByDescending(i => i.History?.LastWatched ?? DateTime.MinValue).ToList();
                var cards = await Task.WhenAll(items.Select(async i => i.IsLive || i.Type == MediaType.Channel ? await ChannelCardAsync(i).ConfigureAwait(false) : ItemCard(i))).ConfigureAwait(false);
                return cards.Where(c => c != null).Select(c => c!).ToList();
            }
            catch (Exception ex) { Console.WriteLine($"[HomeData] Continue watching failed: {ex.Message}"); return Array.Empty<HomeCard>(); }
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
