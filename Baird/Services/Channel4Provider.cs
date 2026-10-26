using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baird.Models;

namespace Baird.Services
{
    /// <summary>
    /// Channel 4 in Baird's normal search. Unlike Netflix and Disney+, Channel 4's search and programme pages are public, so results
    /// and episode lists come straight over HTTP (<see cref="Channel4Catalogue"/>), even while the shared browser plays something.
    /// Only playing needs the signed-in browser (<see cref="Channel4PlaybackEngine"/>).
    /// </summary>
    public class Channel4Provider : IMediaProvider
    {
        public const string SourceName = "Channel 4";
        public const string IdPrefix = "channel4:";
        /// <summary>Id prefix of an episode item ("channel4:ep:{programmeId}"): no '|', so Baird's season and continuous-play code leaves it whole.</summary>
        public const string EpisodeIdPrefix = IdPrefix + "ep:";
        public const string FilmLabel = "Film";

        private readonly Channel4Catalogue _catalogue;

        public Channel4Provider(Channel4Catalogue catalogue) => _catalogue = catalogue;

        public string Name => SourceName;

        public static bool IsChannel4(string? source) => source == SourceName;

        public async Task<IEnumerable<MediaItemData>> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query)) return Enumerable.Empty<MediaItemData>();
            // A failure throws: the search screen marks the provider as failed and the journal has the reason.
            var results = await _catalogue.SearchAsync(query.Trim(), cancellationToken);
            // Channel 4 also lists programmes it has nothing to watch for ("No episodes available to watch on demand").
            return results.Where(r => r.Available).Select(ToMediaItem).ToList();
        }

        public static MediaItemData ToMediaItem(Channel4SearchResult r)
        {
            var film = r.LooksLikeFilm;
            return new MediaItemData
            {
                Id = IdPrefix + r.Slug,
                Name = r.Title,
                Details = film ? FilmLabel : "",
                ImageUrl = r.ImageUrl ?? "",
                IsLive = false,
                // A series is a container (its series and episodes are its children); a film plays from its programme page, which the
                // engine reads for the one thing to play.
                StreamUrl = film ? Channel4Catalogue.ProgrammeUrl(r.Slug) : "",
                Source = SourceName,
                Type = film ? MediaType.Video : MediaType.Brand,
                Synopsis = r.Description,
                Subtitle = "",
            };
        }

        /// <summary>
        /// The series and episodes of a programme, the way Jellyfin does it: <c>channel4:{slug}</c> gives the series folders (or the
        /// episodes when there is one series), <c>channel4:{slug}|{series}</c> gives that series' episodes. Answers only for
        /// "channel4:" ids that are not episodes; a Channel 4 failure throws, which the data service logs and skips.
        /// </summary>
        public async Task<IEnumerable<MediaItemData>> GetChildrenAsync(string id)
        {
            if (!id.StartsWith(IdPrefix, StringComparison.Ordinal) || id.StartsWith(EpisodeIdPrefix, StringComparison.Ordinal))
                return Enumerable.Empty<MediaItemData>();

            var slug = id[IdPrefix.Length..];
            int? targetSeries = null;
            var bar = slug.IndexOf('|');
            if (bar >= 0)
            {
                if (int.TryParse(slug[(bar + 1)..], out var n)) targetSeries = n;
                slug = slug[..bar];
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var programme = await _catalogue.GetProgrammeAsync(slug, timeout.Token);

            if (targetSeries == null && programme.SeriesNumbers.Count > 1)
            {
                // Newest series first, as Channel 4 lists them.
                return programme.SeriesNumbers.OrderByDescending(n => n).Select(n =>
                {
                    var episodes = programme.Episodes.Where(e => e.Series == n).ToList();
                    var name = $"Series {n}";
                    return new MediaItemData
                    {
                        Id = $"{IdPrefix}{slug}|{n}",
                        Name = name,
                        Details = episodes.Count == 1 ? "1 Episode" : $"{episodes.Count} Episodes",
                        ImageUrl = episodes.Select(e => e.ImageUrl).FirstOrDefault(i => i != null) ?? "",
                        IsLive = false,
                        StreamUrl = "",
                        Source = SourceName,
                        Type = MediaType.Brand,
                        Synopsis = name,
                        Subtitle = name,
                    };
                }).ToList();
            }

            var wanted = targetSeries == null ? programme.Episodes : programme.Episodes.Where(e => e.Series == targetSeries).ToList();
            return wanted.Select(e => ToEpisodeItem(e, programme.IsFilm)).ToList();
        }

        public static MediaItemData ToEpisodeItem(Channel4Episode e, bool isFilm = false) => new()
        {
            Id = EpisodeIdPrefix + e.ProgrammeId,
            Name = e.Title,
            Details = isFilm ? FilmLabel : $"S{e.Series}:E{e.Number}",
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
