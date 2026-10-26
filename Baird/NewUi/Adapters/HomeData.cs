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

        public HomeData(IDataService data, IEpgService epg, IJellyseerrService? seerr)
        {
            _data = data; _epg = epg; _seerr = seerr;
        }

        public static PlayVia ViaOf(string source) =>
            source.StartsWith("Netflix", StringComparison.OrdinalIgnoreCase) || source.StartsWith("Disney", StringComparison.OrdinalIgnoreCase) ? PlayVia.Browser : PlayVia.Mpv;

        private static HomeCard ItemCard(MediaItemViewModel m) =>
            new(m.Type == MediaType.Brand ? CardKind.Series : !string.IsNullOrWhiteSpace(m.Subtitle) ? CardKind.Episode : CardKind.Film,
                m.Id, m.Name,
                m.Type == MediaType.Brand ? m.Details : !string.IsNullOrWhiteSpace(m.Subtitle) ? m.Subtitle : m.Details,
                string.IsNullOrEmpty(m.ImageUrl) ? null : m.ImageUrl,
                m.History is { } h && !h.IsFinished && h.LastPosition > TimeSpan.Zero ? m.Progress : null,
                null, m.Source, m.StreamUrl is { Length: > 0 } ? ViaOf(m.Source) : PlayVia.None, m.StreamUrl, m.LastPosition, Synopsis: m.Synopsis);

        /// <summary>What you're part-way through (not live channels), most recent first.</summary>
        public async Task<IReadOnlyList<HomeCard>> UpNextAsync()
        {
            try
            {
                var items = await _data.GetContinueWatchingAsync().ConfigureAwait(false);
                return items.Where(i => !i.IsLive && i.Type != MediaType.Channel)
                    .OrderByDescending(i => i.History?.LastWatched ?? DateTime.MinValue)
                    .Select(ItemCard).ToList();
            }
            catch (Exception ex) { Console.WriteLine($"[HomeData] Up next failed: {ex.Message}"); return Array.Empty<HomeCard>(); }
        }

        /// <summary>Live channels in channel-number order, each with what's on now (when the guide answers in time).</summary>
        public async Task<IReadOnlyList<HomeCard>> OnNowAsync(DateTimeOffset now)
        {
            IEnumerable<MediaItemViewModel> listing;
            try { listing = await _data.GetListingAsync().ConfigureAwait(false); }
            catch (Exception ex) { Console.WriteLine($"[HomeData] Listing failed: {ex.Message}"); return Array.Empty<HomeCard>(); }

            var channels = listing.Where(i => i.IsLive && !string.IsNullOrEmpty(i.ChannelNumber) && i.ChannelNumber != "0" && !string.IsNullOrEmpty(i.StreamUrl))
                .OrderBy(i => int.TryParse(i.ChannelNumber, out var n) ? n : int.MaxValue)
                .Select(i => new Channel(i.Id, i.ChannelNumber!, i.Name, string.IsNullOrEmpty(i.ImageUrl) ? null : i.ImageUrl, i.StreamUrl!))
                .ToList();

            var programmes = await Task.WhenAll(channels.Select(c => CurrentProgrammeAsync(c.Id))).ConfigureAwait(false);
            return channels.Select((c, k) =>
            {
                var p = programmes[k];
                return new HomeCard(CardKind.Channel, c.Id, $"{c.Number}  {c.Name}", p?.Title, c.LogoUrl, p?.ProgressAt(now), "live",
                    "TV", PlayVia.Mpv, c.StreamUrl, Channel: c, Programme: p, Synopsis: p?.Synopsis);
            }).ToList();
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

        public static Programme? Map(EpgProgram? p) => p == null || p.StartUnix <= 0 ? null :
            new Programme(p.Id, p.ChannelId, p.Title, string.IsNullOrWhiteSpace(p.Subtitle) ? null : p.Subtitle,
                string.IsNullOrWhiteSpace(p.DisplayDetails) ? null : p.DisplayDetails,
                DateTimeOffset.FromUnixTimeSeconds(p.StartUnix), DateTimeOffset.FromUnixTimeSeconds(p.StopUnix));

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
