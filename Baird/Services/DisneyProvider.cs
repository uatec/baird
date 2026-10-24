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
            var kind = r.Kind switch { "series" => SeriesLabel, "movie" => FilmLabel, _ => r.Kind ?? "" };
            var details = string.Join(" · ", new[] { kind, r.Year, r.Rating }.Where(x => !string.IsNullOrWhiteSpace(x)));
            return new MediaItemData
            {
                Id = IdPrefix + r.EntityId,
                Name = r.Title,
                Details = details,
                ImageUrl = r.ImageUrl ?? "",
                IsLive = false,
                // The entity page, not a play URL: a series id is not a play id. The engine reads the page to find what to play.
                StreamUrl = DisneySearchParser.EntityStreamUrl(r.EntityId, r.Kind == "movie"),
                Source = SourceName,
                Type = MediaType.Video,
                Synopsis = r.Description ?? "",
                Subtitle = "",
                Duration = r.RuntimeSeconds is > 0 ? TimeSpan.FromSeconds(r.RuntimeSeconds.Value) : TimeSpan.Zero,
            };
        }

        public Task<IEnumerable<MediaItemData>> GetListingAsync() => Task.FromResult(Enumerable.Empty<MediaItemData>());

        public Task<IEnumerable<MediaItemData>> GetChildrenAsync(string id) => Task.FromResult(Enumerable.Empty<MediaItemData>());

        // No lookup by id without a search; history and Continue Watching use Baird's own item cache.
        public Task<MediaItemData?> GetItemAsync(string id) => Task.FromResult<MediaItemData?>(null);
    }
}
