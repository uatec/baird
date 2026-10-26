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
    /// One search for everything: the providers (shows, films, channels), Seerr (titles you can request) and the guide
    /// (what's on). Results are grouped by title, not by provider: the same show on Jellyfin and iPlayer is one result
    /// that says where it's available, and a Seerr match for it adds what can be requested. Each source that fails or
    /// is slow contributes nothing rather than holding the rest back.
    /// </summary>
    public sealed class CatalogueSearch
    {
        private readonly IDataService _data;
        private readonly IEpgService _epg;
        private readonly IJellyseerrService? _seerr;
        private readonly ISearchHistoryService? _history;
        private readonly TimeSpan _timeout;
        private (DateTimeOffset At, IReadOnlyDictionary<string, IReadOnlyList<EpgProgram>> Schedules)? _guide;
        private static readonly TimeSpan GuideMaxAge = TimeSpan.FromMinutes(10);
        public const int MaxSeerrOnly = 12;

        public CatalogueSearch(IDataService data, IEpgService epg, IJellyseerrService? seerr, ISearchHistoryService? history = null, TimeSpan? timeout = null)
        {
            _data = data; _epg = epg; _seerr = seerr; _history = history; _timeout = timeout ?? TimeSpan.FromSeconds(8);
        }

        // ------------------------------------------------------------------ names, services
        /// <summary>"Jellyfin: host" → "Jellyfin"; TvHeadend's channels are "Live TV".</summary>
        public static string ServiceOf(string source) => source.Split(':')[0].Trim();

        /// <summary>Your own copy first, then other players, then the browser services.</summary>
        public static int ServiceRank(string service) => service switch
        {
            "Jellyfin" => 0,
            "BBC iPlayer" => 1,
            "YouTube" => 3,
            _ => HomeData.ViaOf(service) == PlayVia.Browser ? 4 : 2,
        };

        public static int? YearOf(string? text) =>
            text is { Length: >= 4 } && int.TryParse(text[..4], out var y) && y > 1900 && y < 2200 ? y : null;

        private static bool SameTitle(CatalogueTitle t, TitleKind kind, string name, int? year, string? subtitle = null) =>
            t.Kind == kind && SearchRelevance.Normalize(t.Name) == SearchRelevance.Normalize(name)
            && (t.Year == null || year == null || Math.Abs(t.Year.Value - year.Value) <= 1)
            && (kind != TitleKind.Episode || SearchRelevance.Normalize(t.Subtitle) == SearchRelevance.Normalize(subtitle));

        private static TitleKind KindOf(MediaItemViewModel m) =>
            m.Type is MediaType.Brand or MediaType.Folder ? TitleKind.Series : !string.IsNullOrWhiteSpace(m.Subtitle) ? TitleKind.Episode : TitleKind.Film;

        public static TitleSource SourceOf(MediaItemViewModel m)
        {
            var service = ServiceOf(m.Source);
            var unfinished = m.History is { } h && !h.IsFinished && h.LastPosition > TimeSpan.Zero;
            return new TitleSource(service, m.Id, m.Type == MediaType.Brand ? HomeData.ViaOf(m.Source) : m.StreamUrl is { Length: > 0 } ? HomeData.ViaOf(m.Source) : PlayVia.None,
                m.StreamUrl is { Length: > 0 } s ? s : null, unfinished ? m.LastPosition : TimeSpan.Zero, unfinished ? m.Progress : null, m.Duration);
        }

        // ------------------------------------------------------------------ grouping
        /// <summary>
        /// Group provider items and Seerr results by title. Channels are returned separately. The same title from several
        /// providers merges (sources best first); a Seerr result for a title you have attaches to it, and one you don't
        /// becomes a title with nothing to play (requestable, requested, or in the library per Seerr).
        /// </summary>
        public static (List<CatalogueTitle> Titles, List<MediaItemViewModel> Channels) Group(IEnumerable<MediaItemViewModel> items, IEnumerable<JellyseerrSearchResult> seerr)
        {
            var titles = new List<CatalogueTitle>();
            var channels = new List<MediaItemViewModel>();
            foreach (var m in items)
            {
                if (m.IsLive || m.Type == MediaType.Channel)
                {
                    if (channels.All(c => c.Id != m.Id)) channels.Add(m);
                    continue;
                }
                if (m.Type == MediaType.Audio || string.IsNullOrWhiteSpace(m.Name)) continue;
                var kind = KindOf(m);
                var year = YearOf(m.Details);
                var src = SourceOf(m);
                var i = titles.FindIndex(t => SameTitle(t, kind, m.Name, year, m.Subtitle));
                if (i < 0)
                {
                    titles.Add(new CatalogueTitle($"{kind}:{SearchRelevance.Normalize(m.Name)}:{year}:{m.Id}", kind, m.Name, year,
                        string.IsNullOrEmpty(m.ImageUrl) ? null : m.ImageUrl, string.IsNullOrWhiteSpace(m.Synopsis) ? null : m.Synopsis,
                        new[] { src }, Subtitle: string.IsNullOrWhiteSpace(m.Subtitle) ? null : m.Subtitle));
                    continue;
                }
                var t = titles[i];
                if (t.Sources.Any(s => s.ItemId == src.ItemId)) continue;
                titles[i] = t with
                {
                    Year = t.Year ?? year,
                    ImageUrl = t.ImageUrl ?? (string.IsNullOrEmpty(m.ImageUrl) ? null : m.ImageUrl),
                    Synopsis = t.Synopsis ?? (string.IsNullOrWhiteSpace(m.Synopsis) ? null : m.Synopsis),
                    Sources = t.Sources.Append(src).OrderBy(s => ServiceRank(s.Service)).ToList(),
                };
            }

            var seerrOnly = 0;
            foreach (var r in seerr)
            {
                var kind = r.MediaType == "tv" ? TitleKind.Series : r.MediaType == "movie" ? TitleKind.Film : (TitleKind?)null;
                if (kind == null || string.IsNullOrWhiteSpace(r.Title)) continue;
                var year = YearOf(r.ReleaseDate);
                var poster = r.PosterPath is { Length: > 0 } pp ? $"https://image.tmdb.org/t/p/w500{pp}" : null;
                var seerrRef = new SeerrRef(r.Id, r.MediaType, r.MediaInfoStatus == 0 ? 1 : r.MediaInfoStatus);
                var i = titles.FindIndex(t => t.Seerr == null && SameTitle(t, kind.Value, r.Title, year));
                if (i >= 0)
                {
                    var t = titles[i];
                    titles[i] = t with { Seerr = seerrRef, Year = t.Year ?? year, ImageUrl = t.ImageUrl ?? poster, Synopsis = t.Synopsis ?? r.Overview };
                    continue;
                }
                if (titles.Any(t => t.Seerr?.TmdbId == r.Id && t.Seerr.MediaType == r.MediaType) || seerrOnly >= MaxSeerrOnly) continue;
                seerrOnly++;
                titles.Add(new CatalogueTitle($"tmdb:{r.MediaType}:{r.Id}", kind.Value, r.Title, year, poster, string.IsNullOrWhiteSpace(r.Overview) ? null : r.Overview,
                    Array.Empty<TitleSource>(), seerrRef));
            }
            return (titles, channels);
        }

        // ------------------------------------------------------------------ cards
        public static HomeCard CardOf(CatalogueTitle t)
        {
            var best = t.Best;
            var badge = t.Availability switch
            {
                Availability.Get => "get",
                Availability.Requested => "requested",
                Availability.Partial => "partial",
                _ => null,
            };
            var sub = t.Kind == TitleKind.Episode ? t.Subtitle : $"{(t.Year is { } y ? $"{y} · " : "")}{t.Where}";
            return new HomeCard(t.Kind switch { TitleKind.Series => CardKind.Series, TitleKind.Episode => CardKind.Episode, _ => CardKind.Film },
                best?.ItemId ?? t.Key, t.Name, sub, t.ImageUrl, best?.Progress, badge, best?.Service ?? "Seerr",
                best?.Via ?? PlayVia.None, best?.StreamUrl, best?.Resume ?? TimeSpan.Zero, Synopsis: t.Synopsis, Title: t);
        }

        private static HomeCard ChannelCard(Channel c, Programme? p, DateTimeOffset now) =>
            new(CardKind.Channel, c.Id, $"{c.Number}  {c.Name}", p?.Title, c.LogoUrl, p?.ProgressAt(now), "live", "TV", PlayVia.Mpv, c.StreamUrl, Channel: c, Programme: p, Synopsis: p?.Synopsis);

        // ------------------------------------------------------------------ the search
        private async Task<T> Within<T>(Func<CancellationToken, Task<T>> call, T fallback, string what, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                var task = call(cts.Token);
                var done = await Task.WhenAny(task, Task.Delay(_timeout, cts.Token)).ConfigureAwait(false);
                if (done == task) return await task.ConfigureAwait(false);
                Console.WriteLine($"[CatalogueSearch] {what} didn't answer within {_timeout.TotalSeconds:0} s");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Console.WriteLine($"[CatalogueSearch] {what} failed: {ex.Message}"); }
            finally { cts.Cancel(); }
            return fallback;
        }

        /// <summary>
        /// Search everything. <paramref name="channels"/> are the live channels (for numbers and the guide); channel
        /// results are those whose name or number matches.
        /// </summary>
        public async Task<SearchResults> SearchAsync(string query, IReadOnlyList<Channel> channels, DateTimeOffset now, CancellationToken ct = default)
        {
            var q = query.Trim();
            if (q.Length == 0) return SearchResults.Empty;
            var library = Within<IEnumerable<MediaItemViewModel>>(c => _data.SearchAsync(q, c), Array.Empty<MediaItemViewModel>(), "Library search", ct);
            var seerr = _seerr == null || q.All(char.IsDigit)
                ? Task.FromResult<IEnumerable<JellyseerrSearchResult>>(Array.Empty<JellyseerrSearchResult>())
                : Within(c => _seerr.SearchAsync(q, 1, c), Enumerable.Empty<JellyseerrSearchResult>(), "Seerr search", ct);
            var listings = Within(c => ListingsAsync(q, channels, now, c), (IReadOnlyList<(Channel, Programme)>)Array.Empty<(Channel, Programme)>(), "Guide search", ct);
            await Task.WhenAll(library, seerr, listings).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var results = Assemble(q, library.Result, seerr.Result, listings.Result, channels, now);
            return await WithProgrammesAsync(results, now, ct).ConfigureAwait(false);
        }

        /// <summary>Channel results say what's on (the guide is asked for the first few, briefly).</summary>
        private async Task<SearchResults> WithProgrammesAsync(SearchResults r, DateTimeOffset now, CancellationToken ct)
        {
            async Task<SearchHit> Fill(SearchHit h)
            {
                if (h.Kind != HitKind.Channel || h.Card.Programme != null || h.Card.Channel is not { } c) return h;
                var p = await Within(async _ => HomeData.Map(await _epg.GetCurrentProgrammeAsync(c.Id).ConfigureAwait(false)), null, $"Guide for {c.Name}", ct).ConfigureAwait(false);
                return p == null ? h : h with { Card = ChannelCard(c, p, now) };
            }
            var top = r.Top == null ? null : await Fill(r.Top).ConfigureAwait(false);
            var chans = await Task.WhenAll(r.Channels.Select((h, i) => i < 9 ? Fill(h) : Task.FromResult(h))).ConfigureAwait(false);
            return r with { Top = top, Channels = chans };
        }

        /// <summary>Builds the grouped results (separate from the I/O so it can be tested on its own).</summary>
        public static SearchResults Assemble(string q, IEnumerable<MediaItemViewModel> library, IEnumerable<JellyseerrSearchResult> seerr,
            IReadOnlyList<(Channel Channel, Programme Programme)> listings, IReadOnlyList<Channel> channels, DateTimeOffset now)
        {
            var (titles, liveItems) = Group(library, seerr);
            var digits = q.All(char.IsDigit);

            // Channels: the provider's live matches, as the channels we know (with what's on), else as they came.
            var chanHits = new List<SearchHit>();
            foreach (var m in liveItems)
            {
                var c = channels.FirstOrDefault(x => x.Id == m.Id) ?? (m.StreamUrl is { Length: > 0 } s ? new Channel(m.Id, m.ChannelNumber ?? "", m.Name, string.IsNullOrEmpty(m.ImageUrl) ? null : m.ImageUrl, s) : null);
                if (c == null || c.Number is "" or "0") continue;
                var p = listings.FirstOrDefault(l => l.Channel.Id == c.Id && l.Programme.Start <= now && now < l.Programme.End).Programme;
                var score = digits ? (c.Number == q ? 100 : c.Number.StartsWith(q, StringComparison.Ordinal) ? 90 - c.Number.Length : 0) : SearchRelevance.Score(c.Name, q);
                if (score <= 0 && !digits) score = 1;
                chanHits.Add(new SearchHit(HitKind.Channel, ChannelCard(c, p, now), Score: score));
            }
            // A number typed matches the channel numbers we know even if the provider didn't say so.
            if (digits)
                foreach (var c in channels.Where(c => c.Number.StartsWith(q, StringComparison.Ordinal) && chanHits.All(h => h.Card.Id != c.Id)))
                    chanHits.Add(new SearchHit(HitKind.Channel, ChannelCard(c, null, now), Score: c.Number == q ? 100 : 90 - c.Number.Length));
            chanHits = chanHits.OrderByDescending(h => h.Score).ThenBy(h => int.TryParse(h.Card.Channel?.Number, out var n) ? n : int.MaxValue).ToList();

            var titleHits = titles
                .Select(t => new SearchHit(HitKind.Title, CardOf(t), t, SearchRelevance.Score(t.Name, q)))
                .Where(h => h.Score > 0 || h.Title!.Sources.Count > 0)
                .OrderByDescending(h => h.Score)
                .ThenBy(h => h.Title!.Availability is Availability.Play or Availability.Partial ? 0 : 1)
                .ThenBy(h => h.Title!.Best is { } b ? ServiceRank(b.Service) : 9)
                .ToList();

            var onTv = listings.Select(l =>
            {
                var live = l.Programme.Start <= now && now < l.Programme.End;
                var day = l.Programme.Start.LocalDateTime.Date == now.LocalDateTime.Date ? "" : l.Programme.Start.LocalDateTime.Date == now.LocalDateTime.Date.AddDays(1) ? "Tomorrow " : $"{l.Programme.Start.LocalDateTime:ddd} ";
                return new SearchHit(HitKind.Listing, new HomeCard(CardKind.Episode, l.Programme.Id, l.Programme.Title,
                    $"{l.Channel.Name} · {(live ? "on now" : $"{day}{l.Programme.Start.LocalDateTime:HH:mm}")}", l.Channel.LogoUrl,
                    live ? l.Programme.ProgressAt(now) : null, live ? "live" : null, "TV", PlayVia.Mpv, l.Channel.StreamUrl,
                    Channel: l.Channel, Programme: l.Programme, Synopsis: l.Programme.Synopsis), Score: SearchRelevance.Score(l.Programme.Title, q));
            }).ToList();

            SearchHit? top = digits
                ? chanHits.FirstOrDefault() ?? titleHits.FirstOrDefault()
                : new[] { titleHits.FirstOrDefault(), chanHits.FirstOrDefault(h => h.Score >= 55) }.Where(h => h != null).OrderByDescending(h => h!.Score).FirstOrDefault()
                  ?? onTv.FirstOrDefault(h => h.Card.Badge == "live") ?? onTv.FirstOrDefault();
            return new SearchResults(q, top, titleHits.Where(h => h != top).ToList(), chanHits.Where(h => h != top).ToList(), onTv.Where(h => h != top).ToList());
        }

        // ------------------------------------------------------------------ the guide
        /// <summary>Every query word starts a word of the text ("doc wh" finds "Doctor Who").</summary>
        public static bool WordsMatch(string query, string text)
        {
            var q = SearchRelevance.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var w = SearchRelevance.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return q.Length > 0 && q.All(x => w.Any(y => y.StartsWith(x, StringComparison.Ordinal)));
        }

        /// <summary>
        /// Guide listings whose title matches: one per channel (what's on now, else the next showing), on now first.
        /// The schedules are read once and kept for a few minutes (the guide doesn't change while you type).
        /// </summary>
        public async Task<IReadOnlyList<(Channel, Programme)>> ListingsAsync(string query, IReadOnlyList<Channel> channels, DateTimeOffset now, CancellationToken ct = default)
        {
            if (channels.Count == 0 || query.All(char.IsDigit)) return Array.Empty<(Channel, Programme)>();
            if (_guide is not { } g || now - g.At > GuideMaxAge)
            {
                var schedules = await _epg.GetBulkScheduleAsync(channels.Select(c => c.Id), 40, ct).ConfigureAwait(false);
                _guide = g = (now, schedules);
            }
            var found = new List<(Channel, Programme)>();
            foreach (var c in channels)
            {
                if (!g.Schedules.TryGetValue(c.Id, out var list)) continue;
                var best = list.Select(HomeData.Map).Where(p => p != null && p.End > now && WordsMatch(query, p.Title))
                    .OrderBy(p => p!.Start).FirstOrDefault();
                if (best != null) found.Add((c, best));
            }
            return found.OrderBy(f => f.Item2.Start <= now ? 0 : 1).ThenBy(f => f.Item2.Start).Take(9).ToList();
        }

        // ------------------------------------------------------------------ recent searches
        public async Task<IReadOnlyList<string>> RecentAsync()
        {
            if (_history == null) return Array.Empty<string>();
            try { return (await _history.GetSuggestedTermsAsync(6).ConfigureAwait(false)).ToList(); }
            catch (Exception ex) { Console.WriteLine($"[CatalogueSearch] Recent searches failed: {ex.Message}"); return Array.Empty<string>(); }
        }

        public void Remember(string query)
        {
            if (_history == null || string.IsNullOrWhiteSpace(query)) return;
            _ = Task.Run(async () =>
            {
                try { await _history.AddSearchTermAsync(query.Trim()).ConfigureAwait(false); }
                catch (Exception ex) { Console.WriteLine($"[CatalogueSearch] Saving the search failed: {ex.Message}"); }
            });
        }

        /// <summary>Library titles matching a name (to find where a Seerr title or a card's show can be watched).</summary>
        public async Task<IReadOnlyList<CatalogueTitle>> LibraryTitlesAsync(string name, CancellationToken ct = default)
        {
            var items = await Within<IEnumerable<MediaItemViewModel>>(c => _data.SearchAsync(name, c), Array.Empty<MediaItemViewModel>(), "Library lookup", ct).ConfigureAwait(false);
            return Group(items, Array.Empty<JellyseerrSearchResult>()).Titles;
        }

        /// <summary>Seerr's entry for a title (same kind, same name, year within one), if any.</summary>
        public async Task<SeerrRef?> FindInSeerrAsync(CatalogueTitle t, CancellationToken ct = default)
        {
            if (_seerr == null || t.Kind == TitleKind.Episode) return null;
            var results = await Within(c => _seerr.SearchAsync(t.Name, 1, c), Enumerable.Empty<JellyseerrSearchResult>(), "Seerr lookup", ct).ConfigureAwait(false);
            var type = t.Kind == TitleKind.Series ? "tv" : "movie";
            var match = results.FirstOrDefault(r => r.MediaType == type && SameTitle(t, t.Kind, r.Title, YearOf(r.ReleaseDate)));
            return match == null ? null : new SeerrRef(match.Id, match.MediaType, match.MediaInfoStatus == 0 ? 1 : match.MediaInfoStatus);
        }

        public static bool Matches(CatalogueTitle a, CatalogueTitle b) => SameTitle(a, b.Kind, b.Name, b.Year, b.Subtitle);
    }
}
