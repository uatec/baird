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
    /// are its children); a film or a special plays from its page.
    /// </summary>
    public class ItvxProvider : IMediaProvider
    {
        public const string SourceName = "ITVX";
        public const string IdPrefix = "itvx:";
        /// <summary>Id prefix of a playable item ("itvx:ep:{id}"): no '|', so Baird's season and continuous-play code leaves it whole.</summary>
        public const string EpisodeIdPrefix = IdPrefix + "ep:";
        public const string FilmLabel = "Film";

        private readonly ItvxCatalogue _catalogue;

        public ItvxProvider(ItvxCatalogue catalogue) => _catalogue = catalogue;

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

        // Episode lists come from the programme page, which only a real browser can read; not done yet.
        public Task<IEnumerable<MediaItemData>> GetChildrenAsync(string id) => Task.FromResult(Enumerable.Empty<MediaItemData>());

        public Task<IEnumerable<MediaItemData>> GetListingAsync() => Task.FromResult(Enumerable.Empty<MediaItemData>());

        // No lookup by id without a search; history and Continue Watching use Baird's own item cache.
        public Task<MediaItemData?> GetItemAsync(string id) => Task.FromResult<MediaItemData?>(null);
    }
}
