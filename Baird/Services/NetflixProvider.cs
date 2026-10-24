using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baird.Models;

namespace Baird.Services
{
    /// <summary>
    /// Netflix in Baird's normal search. Results come from the signed-in session (<see cref="NetflixSession"/>), so only titles
    /// this account can watch appear. Playing one goes through Baird's normal player, which plays it in the Netflix browser (NetflixPlaybackEngine).
    /// </summary>
    public class NetflixProvider : IMediaProvider
    {
        public const string SourceName = "Netflix";
        public const string IdPrefix = "netflix:";

        private readonly NetflixSession _session;

        public NetflixProvider(NetflixSession session) => _session = session;

        public string Name => SourceName;

        public static bool IsNetflix(string? source) => source == SourceName;

        public static string WatchUrl(string titleId) => $"https://www.netflix.com/watch/{titleId}";

        public async Task<IEnumerable<MediaItemData>> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query)) return Enumerable.Empty<MediaItemData>();
            if (_session.Hub.ActivePlayback is { } playing)
            {
                // The one shared browser is playing (Netflix or Disney+); searching in it would disturb the video. Not a failure.
                Console.WriteLine($"[NetflixProvider] {playing} is playing; no Netflix results until it stops.");
                return Enumerable.Empty<MediaItemData>();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var outcome = await _session.SearchAsync(query.Trim(), timeout.Token);
            if (!outcome.Ok)
            {
                // Loud: the search screen marks the provider as failed and the journal has the reason.
                throw new InvalidOperationException($"Netflix search failed: {outcome.Failure}");
            }
            return outcome.Reply!.Results.Select(ToMediaItem).ToList();
        }

        public static MediaItemData ToMediaItem(NetflixSearchResult r) => new()
        {
            Id = IdPrefix + r.TitleId,
            Name = r.Title,
            Details = r.Kind switch { "Show" => "Series", "Movie" => "Film", _ => r.Kind ?? "" },
            ImageUrl = r.ImageUrl ?? "",
            IsLive = false,
            // A series id plays the episode Netflix would play next for this profile.
            StreamUrl = WatchUrl(r.TitleId),
            Source = SourceName,
            Type = MediaType.Video,
            Synopsis = "",
            Subtitle = "",
        };

        public Task<IEnumerable<MediaItemData>> GetListingAsync() => Task.FromResult(Enumerable.Empty<MediaItemData>());

        public Task<IEnumerable<MediaItemData>> GetChildrenAsync(string id) => Task.FromResult(Enumerable.Empty<MediaItemData>());

        // No lookup by id without a search; history and Continue Watching use Baird's own item cache.
        public Task<MediaItemData?> GetItemAsync(string id) => Task.FromResult<MediaItemData?>(null);
    }
}
