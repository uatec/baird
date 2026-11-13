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
        public const string EpisodeIdPrefix = IdPrefix + "ep:";

        private readonly NetflixSession _session;

        public NetflixProvider(NetflixSession session) => _session = session;

        /// <summary>How long a season or episode list may wait for the browser to be ready before the series falls back to "Play next episode".</summary>
        public TimeSpan AskTimeout { get; init; } = TimeSpan.FromSeconds(30);

        public string Name => SourceName;

        public static bool IsNetflix(string? source) => source == SourceName;

        public static string WatchUrl(string titleId) => $"https://www.netflix.com/watch/{titleId}";

        public async Task<IEnumerable<MediaItemData>> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query)) return Enumerable.Empty<MediaItemData>();
            if (_session.Hub.ActivePlayback is { } playing)
            {
                // The one shared browser is playing; searching in it would disturb the video. Ask Netflix directly instead (with the
                // browser's cookies); if that isn't possible or Netflix refuses it, no results (not a failure).
                var direct = await _session.SearchDirectAsync(query.Trim(), cancellationToken);
                if (direct.Ok) return direct.Reply!.Results.Select(ToMediaItem).ToList();
                Console.WriteLine($"[NetflixProvider] {playing} is playing and the direct search didn't work ({direct.Failure}); no Netflix results until it stops.");
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

        public static MediaItemData ToMediaItem(NetflixSearchResult r)
        {
            // A series is a container: it opens the programme page (GetChildrenAsync) instead of playing straight away.
            var isShow = r.Kind == "Show";
            return new()
            {
                Id = IdPrefix + r.TitleId,
                Name = r.Title,
                Details = r.Kind switch { "Show" => "Series", "Movie" => "Film", _ => r.Kind ?? "" },
                ImageUrl = r.ImageUrl ?? "",
                IsLive = false,
                StreamUrl = isShow ? "" : WatchUrl(r.TitleId),
                Source = SourceName,
                Type = isShow ? MediaType.Brand : MediaType.Video,
                Synopsis = "",
                Subtitle = "",
            };
        }

        public Task<IEnumerable<MediaItemData>> GetListingAsync() => Task.FromResult(Enumerable.Empty<MediaItemData>());

        /// <summary>
        /// A series' seasons and episodes, from the requests Netflix's own title page sends: <c>netflix:{show}</c> gives the season
        /// folders (or the episodes when there is one season); <c>netflix:{show}|s{n}:{seasonId}</c> gives a season's episodes.
        /// When Netflix can't be asked (it is playing, not signed in, or it changed its request ids) a series falls back to one
        /// "Play next episode" entry, which plays the episode Netflix would play next for this profile.
        /// </summary>
        public async Task<IEnumerable<MediaItemData>> GetChildrenAsync(string id)
        {
            if (!id.StartsWith(IdPrefix, StringComparison.Ordinal) || id.StartsWith(EpisodeIdPrefix, StringComparison.Ordinal)) return Enumerable.Empty<MediaItemData>();
            var parts = id[IdPrefix.Length..].Split('|');
            var showId = parts[0];
            if (parts.Length == 1) return await SeriesChildrenAsync(id, showId);
            if (parts.Length == 2 && TryParseSeason(parts[1], out var number, out var seasonId))
                return await EpisodesOfAsync(showId, number, seasonId) ?? Enumerable.Empty<MediaItemData>();
            return Enumerable.Empty<MediaItemData>();
        }

        private static bool TryParseSeason(string part, out int number, out string seasonId)
        {
            number = 0; seasonId = "";
            var colon = part.IndexOf(':');
            if (colon < 2 || part[0] != 's' || !int.TryParse(part[1..colon], out number) || !long.TryParse(part[(colon + 1)..], out _)) return false;
            seasonId = part[(colon + 1)..];
            return true;
        }

        private async Task<IEnumerable<MediaItemData>> SeriesChildrenAsync(string id, string showId)
        {
            if (!long.TryParse(showId, out _)) return Enumerable.Empty<MediaItemData>();
            using var timeout = new CancellationTokenSource(AskTimeout);
            NetflixListOutcome<NetflixSeason> seasons;
            try { seasons = await _session.GetSeasonsAsync(showId, timeout.Token); }
            catch (OperationCanceledException) { return PlayNext(id, showId); } // the browser didn't get ready in time
            if (!seasons.Ok || seasons.Items!.Count == 0) return PlayNext(id, showId);
            if (seasons.Items.Count == 1)
                return await EpisodesOfAsync(showId, 1, seasons.Items[0].SeasonId) ?? PlayNext(id, showId);
            return seasons.Items.Select(s => new MediaItemData
            {
                Id = $"{IdPrefix}{showId}|s{s.Number}:{s.SeasonId}",
                Name = $"Season {s.Number}",
                Details = s.EpisodeCount == 1 ? "1 Episode" : $"{s.EpisodeCount} Episodes",
                ImageUrl = "",
                IsLive = false,
                StreamUrl = "",
                Source = SourceName,
                Type = MediaType.Brand,
                Synopsis = "",
                Subtitle = "",
            }).ToList();
        }

        /// <summary>The season's playable episodes, or null when Netflix couldn't be asked.</summary>
        private async Task<IEnumerable<MediaItemData>?> EpisodesOfAsync(string showId, int seasonNumber, string seasonId)
        {
            using var timeout = new CancellationTokenSource(AskTimeout);
            NetflixListOutcome<NetflixEpisode> episodes;
            try { episodes = await _session.GetEpisodesAsync(seasonId, timeout.Token); }
            catch (OperationCanceledException) { return null; }
            if (!episodes.Ok) return null;
            return episodes.Items!.Where(e => e.Playable).Select(e => new MediaItemData
            {
                Id = EpisodeIdPrefix + e.EpisodeId,
                Name = e.Title,
                Details = $"S{seasonNumber}:E{e.Number}",
                ImageUrl = e.ImageUrl ?? "",
                IsLive = false,
                StreamUrl = WatchUrl(e.EpisodeId),
                Source = SourceName,
                Type = MediaType.Video,
                Synopsis = e.Synopsis ?? "",
                Subtitle = "",
                Duration = e.RuntimeSeconds > 0 ? TimeSpan.FromSeconds(e.RuntimeSeconds) : TimeSpan.Zero,
                SeriesId = IdPrefix + showId,
            }).ToList();
        }

        private static IEnumerable<MediaItemData> PlayNext(string id, string showId) => new[]
        {
            new MediaItemData
            {
                Id = id + "|next",
                Name = "Play next episode",
                Details = "Continues where Netflix left off for this profile",
                ImageUrl = "",
                IsLive = false,
                StreamUrl = WatchUrl(showId),
                Source = SourceName,
                Type = MediaType.Video,
                Synopsis = "",
                Subtitle = "",
            },
        };

        // No lookup by id without a search; history and Continue Watching use Baird's own item cache.
        public Task<MediaItemData?> GetItemAsync(string id) => Task.FromResult<MediaItemData?>(null);
    }
}
