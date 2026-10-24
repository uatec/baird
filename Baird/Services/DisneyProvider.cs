using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baird.Models;

namespace Baird.Services
{
    /// <summary>
    /// Disney+ in Baird's normal search. Results come from the signed-in session (<see cref="DisneyService"/>), so only titles
    /// this account, profile and region can watch appear. Playing one goes to the Disney+ playback page, not to mpv (see
    /// MainViewModel.PlayItem). The twin of <see cref="NetflixProvider"/>.
    /// </summary>
    public class DisneyProvider : IMediaProvider
    {
        public const string SourceName = "Disney+";
        public const string IdPrefix = "disney:";
        /// <summary>Id prefix of an episode item ("disney:ep:{playId}"): no '|', so Baird's season and continuous-play code leaves it whole.</summary>
        public const string EpisodeIdPrefix = IdPrefix + "ep:";
        public const string FilmLabel = "Film";
        public const string SeriesLabel = "Series";

        private readonly DisneyService _service;

        public DisneyProvider(DisneyService service) => _service = service;

        public string Name => SourceName;

        public static bool IsDisney(string? source) => source == SourceName;

        /// <summary>The entity id inside a Baird item id ("disney:{entityId}").</summary>
        public static string EntityIdOf(string itemId) => itemId.StartsWith(IdPrefix, StringComparison.Ordinal) ? itemId[IdPrefix.Length..] : itemId;

        /// <summary>True for a film (its entity id is its play id); a series needs its entity page read to find the episode.</summary>
        public static bool IsFilm(MediaItemData item) => item.Details.StartsWith(FilmLabel, StringComparison.Ordinal);

        public async Task<IEnumerable<MediaItemData>> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query)) return Enumerable.Empty<MediaItemData>();
            if (_service.Hub.ActivePlayback is { } playing)
            {
                // The one shared browser is playing (Disney+ or Netflix); searching in it would disturb the video. Not a failure.
                Console.WriteLine($"[DisneyProvider] {playing} is playing; no Disney+ results until it stops.");
                return Enumerable.Empty<MediaItemData>();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var outcome = await _service.SearchAsync(query.Trim(), timeout.Token);
            if (!outcome.Ok)
            {
                // Loud: the search screen marks the provider as failed and the journal has the reason.
                throw new InvalidOperationException($"Disney+ search failed: {outcome.Failure}");
            }
            // Disney+'s search also returns trailers, podcasts and extras; they are listed too, as Disney+ lists them.
            return outcome.Reply!.Results.Select(ToMediaItem).ToList();
        }

        public static MediaItemData ToMediaItem(DisneySearchResult r)
        {
            var isSeries = r.Kind == "series";
            var kind = r.Kind switch { "series" => SeriesLabel, "movie" => FilmLabel, _ => r.Kind ?? "" };
            var details = string.Join(" · ", new[] { kind, r.Year, r.Rating }.Where(x => !string.IsNullOrWhiteSpace(x)));
            return new MediaItemData
            {
                Id = IdPrefix + r.EntityId,
                Name = r.Title,
                Details = details,
                ImageUrl = r.ImageUrl ?? "",
                IsLive = false,
                // A series is a container (its seasons and episodes are its children, see GetChildrenAsync); a film plays from its entity
                // page, which the engine reads for the length (?baird=film lets it play the film by its id if the page cannot be read).
                StreamUrl = isSeries ? "" : DisneySearchParser.EntityStreamUrl(r.EntityId, isFilm: r.Kind == "movie"),
                Source = SourceName,
                Type = isSeries ? MediaType.Brand : MediaType.Video,
                Synopsis = r.Description ?? "",
                Subtitle = "",
                Duration = r.RuntimeSeconds is > 0 ? TimeSpan.FromSeconds(r.RuntimeSeconds.Value) : TimeSpan.Zero,
            };
        }

        /// <summary>
        /// The seasons and episodes of a series, the way Jellyfin does it: <c>disney:{entityId}</c> gives the season folders (or the
        /// episodes when there is one season), <c>disney:{entityId}|{season}</c> gives that season's episodes. Answers only for
        /// "disney:" ids that are not episodes; a Disney+ failure throws, which the data service logs and skips.
        /// </summary>
        public async Task<IEnumerable<MediaItemData>> GetChildrenAsync(string id)
        {
            if (!id.StartsWith(IdPrefix, StringComparison.Ordinal) || id.StartsWith(EpisodeIdPrefix, StringComparison.Ordinal))
                return Enumerable.Empty<MediaItemData>();

            var entityId = id[IdPrefix.Length..];
            int? targetSeason = null;
            var bar = entityId.IndexOf('|');
            if (bar >= 0)
            {
                if (int.TryParse(entityId[(bar + 1)..], out var n)) targetSeason = n;
                entityId = entityId[..bar];
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var (guide, failure) = await _service.GetEpisodeGuideAsync(entityId, timeout.Token);
            if (guide == null) throw new InvalidOperationException($"Disney+ episodes failed: {failure}");

            if (targetSeason == null && guide.Seasons.Count > 1)
            {
                return guide.Seasons.Select(season => new MediaItemData
                {
                    Id = $"{IdPrefix}{entityId}|{season.Number}",
                    Name = season.Name,
                    Details = season.Episodes.Count == 1 ? "1 Episode" : $"{season.Episodes.Count} Episodes",
                    ImageUrl = season.Episodes.Select(e => e.ImageUrl).FirstOrDefault(i => i != null) ?? "",
                    IsLive = false,
                    StreamUrl = "",
                    Source = SourceName,
                    Type = MediaType.Brand,
                    Synopsis = season.Name,
                    Subtitle = season.Name,
                }).ToList();
            }

            var seasons = targetSeason == null ? guide.Seasons : guide.Seasons.Where(s => s.Number == targetSeason);
            return seasons.SelectMany(s => s.Episodes).Select(ToEpisodeItem).ToList();
        }

        public static MediaItemData ToEpisodeItem(DisneyEpisode e) => new()
        {
            Id = EpisodeIdPrefix + e.Id,
            Name = e.Title,
            Details = $"S{e.Season}:E{e.Number}",
            ImageUrl = e.ImageUrl ?? "",
            IsLive = false,
            StreamUrl = e.PlayUrl, // played directly; the engine finds its length from the episode list that was read
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
