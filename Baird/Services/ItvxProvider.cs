using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baird.Models;

namespace Baird.Services
{
    /// <summary>
    /// ITVX in Baird's normal search. Search is ITVX's public API over plain HTTP (<see cref="ItvxCatalogue"/>), so it works even
    /// while the shared browser plays something. ITVX Premium titles are left out. A programme is a container (its series and episodes
    /// are its children, read from its page in the shared browser by <see cref="ItvxService"/>); a film or a special plays from its page.
    /// </summary>
    public class ItvxProvider : IMediaProvider
    {
        public const string SourceName = "ITVX";
        public const string IdPrefix = "itvx:";
        /// <summary>Id prefix of a playable item ("itvx:ep:{id}"): no '|', so Baird's season and continuous-play code leaves it whole.</summary>
        public const string EpisodeIdPrefix = IdPrefix + "ep:";
        public const string FilmLabel = "Film";

        private readonly ItvxCatalogue _catalogue;
        private readonly ItvxService? _service;

        public ItvxProvider(ItvxCatalogue catalogue, ItvxService? service = null)
        {
            _catalogue = catalogue;
            _service = service;
        }

        public string Name => SourceName;

        public static bool IsItvx(string? source) => source == SourceName;

        public async Task<IEnumerable<MediaItemData>> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query)) return Enumerable.Empty<MediaItemData>();
            // A failure throws: the search screen marks the provider as failed and the journal has the reason.
            var results = await _catalogue.SearchAsync(query.Trim(), cancellationToken);
            return results.Select(ToMediaItem).ToList();
        }

        public static MediaItemData ToMediaItem(ItvxSearchResult r)
        {
            var isProgramme = r.Kind == ItvxKind.Programme;
            return new MediaItemData
            {
                // A programme's id carries its page (slug and id): its episodes are read from that page.
                Id = isProgramme ? $"{IdPrefix}{r.Slug}/{r.Id}" : EpisodeIdPrefix + r.Id,
                Name = r.Title,
                Details = r.Kind switch
                {
                    ItvxKind.Programme => r.EpisodeCount == 1 ? "1 Episode" : r.EpisodeCount > 1 ? $"{r.EpisodeCount} Episodes" : "",
                    ItvxKind.Film => FilmLabel,
                    _ => r.ParentTitle ?? "",
                },
                ImageUrl = r.ImageUrl ?? "",
                IsLive = false,
                StreamUrl = isProgramme ? "" : r.WatchUrl,
                Source = SourceName,
                Type = isProgramme ? MediaType.Brand : MediaType.Video,
                Synopsis = r.Description,
                Subtitle = "",
                Duration = r.RuntimeSeconds > 0 ? TimeSpan.FromSeconds(r.RuntimeSeconds) : TimeSpan.Zero,
            };
        }

        /// <summary>
        /// The series and episodes of a programme, the way Jellyfin does it: <c>itvx:{slug}/{id}</c> gives the series folders (or the
        /// episodes when there is one series), <c>itvx:{slug}/{id}|{series}</c> gives that series' episodes. Answers only for programme
        /// ids; an ITVX failure throws, which the data service logs and skips.
        /// </summary>
        public async Task<IEnumerable<MediaItemData>> GetChildrenAsync(string id)
        {
            if (_service == null || !id.StartsWith(IdPrefix, StringComparison.Ordinal) || id.StartsWith(EpisodeIdPrefix, StringComparison.Ordinal))
                return Enumerable.Empty<MediaItemData>();

            var page = id[IdPrefix.Length..];
            string? targetSeries = null;
            var bar = page.IndexOf('|');
            if (bar >= 0)
            {
                targetSeries = page[(bar + 1)..];
                page = page[..bar];
            }
            var slash = page.IndexOf('/');
            if (slash <= 0) return Enumerable.Empty<MediaItemData>();
            var slug = page[..slash];
            var ccid = page[(slash + 1)..];

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var programme = await _service.GetProgrammeAsync(slug, ccid, timeout.Token);

            if (targetSeries == null && programme.Series.Count > 1)
            {
                // In ITVX's order (oldest series first), with ITVX's labels.
                return programme.Series.Select(series =>
                {
                    var episodes = programme.Episodes.Where(e => e.Series == series.Key).ToList();
                    return new MediaItemData
                    {
                        Id = $"{IdPrefix}{slug}/{ccid}|{series.Key}",
                        Name = series.Label,
                        Details = episodes.Count == 1 ? "1 Episode" : $"{episodes.Count} Episodes",
                        ImageUrl = episodes.Select(e => e.ImageUrl).FirstOrDefault(i => i != null) ?? "",
                        IsLive = false,
                        StreamUrl = "",
                        Source = SourceName,
                        Type = MediaType.Brand,
                        Synopsis = series.Label,
                        Subtitle = series.Label,
                    };
                }).ToList();
            }

            var wanted = targetSeries == null ? programme.Episodes : programme.Episodes.Where(e => e.Series == targetSeries).ToList();
            return wanted.Select(ToEpisodeItem).ToList();
        }

        public static MediaItemData ToEpisodeItem(ItvxEpisode e) => new()
        {
            Id = EpisodeIdPrefix + e.Ccid,
            Name = e.Title,
            Details = e.Label,
            ImageUrl = e.ImageUrl ?? "",
            IsLive = false,
            StreamUrl = e.PlayUrl,
            Source = SourceName,
            Type = MediaType.Video,
            Synopsis = e.Description ?? "",
            Subtitle = "",
            Duration = e.RuntimeSeconds > 0 ? TimeSpan.FromSeconds(e.RuntimeSeconds) : TimeSpan.Zero,
        };

        public Task<IEnumerable<MediaItemData>> GetListingAsync() => Task.FromResult(Enumerable.Empty<MediaItemData>());

        // No lookup by id without a search; history and Continue Watching use Baird's own item cache.
        public Task<MediaItemData?> GetItemAsync(string id) => Task.FromResult<MediaItemData?>(null);
    }
}
