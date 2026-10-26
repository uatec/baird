using System;
using System.Collections.Generic;
using System.Linq;

namespace Baird.NewUi.Model
{
    public enum TitleKind { Series, Film, Episode }

    /// <summary>
    /// Where a title stands: Play (something streams), Partial (Seerr: some of it is in the library), Requested (on its way),
    /// Library (Seerr says it's in the library but no source answered yet), Get (can be requested), None (nothing known).
    /// </summary>
    public enum Availability { Play, Partial, Requested, Library, Get, None }

    /// <summary>One place a title can be watched: a provider's item.</summary>
    public sealed record TitleSource(string Service, string ItemId, PlayVia Via, string? StreamUrl = null, TimeSpan Resume = default, double? Progress = null, TimeSpan Duration = default);

    /// <summary>The title in Seerr (TMDB id). Status as Seerr reports it: 1 unknown, 2 pending, 3 processing, 4 partly available, 5 available.</summary>
    public sealed record SeerrRef(int TmdbId, string MediaType, int Status);

    /// <summary>
    /// One show or film, whatever services carry it (results are grouped by title, not by provider).
    /// <see cref="Sources"/> is ordered best first: your own Jellyfin copy, then other players, then the browser services.
    /// </summary>
    public sealed record CatalogueTitle(string Key, TitleKind Kind, string Name, int? Year, string? ImageUrl, string? Synopsis,
        IReadOnlyList<TitleSource> Sources, SeerrRef? Seerr = null, string? Subtitle = null)
    {
        public Availability Availability =>
            Sources.Count > 0 ? (Seerr?.Status == 4 && Kind == TitleKind.Series ? Availability.Partial : Availability.Play)
            : Seerr == null ? Availability.None
            : Seerr.Status switch { 2 or 3 => Availability.Requested, 4 => Availability.Partial, 5 => Availability.Library, _ => Availability.Get };

        public TitleSource? Best => Sources.Count > 0 ? Sources[0] : null;

        /// <summary>Where it can be watched, in a few words: "On Jellyfin · also on BBC iPlayer", "Not in your library".</summary>
        public string Where
        {
            get
            {
                var services = Sources.Select(s => s.Service).Distinct().ToList();
                if (services.Count > 0)
                    return $"On {services[0]}{(services.Count > 1 ? $" · also on {string.Join(", ", services.Skip(1))}" : "")}";
                return Availability switch
                {
                    Availability.Requested => "Requested",
                    Availability.Partial => "Partly in your library",
                    Availability.Library => "In your library",
                    Availability.Get => "Not in your library · can be requested",
                    _ => "Not available to stream",
                };
            }
        }
    }

    public enum SeasonState { Available, Partial, Requested, Missing }

    /// <summary>
    /// A season: what's in the library (its episodes) merged with what Seerr knows (<see cref="EpisodeCount"/>, requested,
    /// downloading). Episodes Seerr knows of that aren't in the library are listed as missing or downloading.
    /// <see cref="MoreId"/>: the library id of this season's next page of episodes, when it lists them a page at a time (YouTube); null when all are here.
    /// </summary>
    public sealed record Season(int Number, string Label, SeasonState State, string? Service, IReadOnlyList<Episode> Episodes,
        int? EpisodeCount = null, int InLibrary = 0, double? Download = null, string? MoreId = null)
    {
        /// <summary>A state, never a call to action: "Jellyfin", "6 of 10 on Jellyfin", "Downloading 40%", "Not streaming".</summary>
        public string StateLabel => State switch
        {
            SeasonState.Available => Service ?? "Available",
            SeasonState.Partial => $"{InLibrary} of {EpisodeCount} on {Service ?? "Jellyfin"}",
            SeasonState.Requested => Download is { } d ? $"Downloading {d * 100:0}%" : "Requested",
            _ => "Not streaming",
        };
    }

    public enum EpisodeState { Available, Requested, Missing }

    public sealed record Episode(int Season, int Number, string Label, string? Sublabel, string? ImageUrl, EpisodeState State,
        TitleSource? Source = null, bool Watched = false, double? Download = null, string? Synopsis = null, DateTime? LastWatched = null)
    {
        public string Code => $"S{Season} E{Number}";
    }

    /// <summary>A show page's content: the title, its seasons (empty for a film), and Seerr's state of a film.</summary>
    /// <remarks><see cref="CheckingLibrary"/>: some library source hasn't answered yet, so nothing is called missing.</remarks>
    public sealed record ShowDetails(CatalogueTitle Title, IReadOnlyList<Season> Seasons, SeasonState FilmState = SeasonState.Missing, double? FilmDownload = null,
        bool CheckingLibrary = false)
    {
        public const string Checking = "Checking your library…";

        public IEnumerable<int> MissingSeasons => Seasons.Where(s => s.State == SeasonState.Missing).Select(s => s.Number);

        /// <summary>
        /// What the primary button plays: the episode you're part-way through (Resume), else the one after the last you
        /// finished (Next), else the first available (Play). Null when nothing in the show can play.
        /// </summary>
        public (Episode Episode, string Label)? Next
        {
            get
            {
                var all = Seasons.SelectMany(s => s.Episodes).ToList();
                var last = all.Where(e => e.LastWatched != null).OrderByDescending(e => e.LastWatched).FirstOrDefault();
                if (last != null && !last.Watched && last.Source != null) return (last, $"Resume {last.Code}");
                if (last != null)
                {
                    var next = all.Skip(all.IndexOf(last) + 1).FirstOrDefault(e => e.State == EpisodeState.Available && e.Source != null);
                    if (next != null) return (next, $"Next {next.Code}");
                }
                var first = all.FirstOrDefault(e => e.State == EpisodeState.Available && e.Source != null);
                return first != null ? (first, $"Play {first.Code}") : null;
            }
        }

        /// <summary>"Seasons 1–2 on Jellyfin · Season 3 downloading · Season 4 can be requested".</summary>
        public string Summary
        {
            get
            {
                if (CheckingLibrary && Title.Sources.Count == 0) return Checking;
                if (Title.Kind != TitleKind.Series || Seasons.Count == 0)
                    return Title.Sources.Count > 0 ? Title.Where : FilmState switch
                    {
                        SeasonState.Requested => FilmDownload is { } d ? $"Downloading · {d * 100:0}%" : "Requested",
                        SeasonState.Available => "In your library",
                        _ => Title.Seerr != null ? "Not in your library · can be requested" : "Not available to stream",
                    };
                var parts = new List<string>();
                foreach (var g in Seasons.Where(s => s.State == SeasonState.Available).GroupBy(s => s.Service ?? "your library"))
                    parts.Add(g.Count() == Seasons.Count ? $"On {g.Key}" : $"{Label(g.Select(s => s.Number))} on {g.Key}");
                foreach (var s in Seasons.Where(s => s.State == SeasonState.Partial))
                    parts.Add($"{s.InLibrary} of {s.EpisodeCount} episodes of season {s.Number} on {s.Service ?? "Jellyfin"}");
                var req = Seasons.Where(s => s.State == SeasonState.Requested).Select(s => s.Number).ToList();
                if (req.Count > 0) parts.Add($"{Label(req)} downloading");
                var miss = MissingSeasons.ToList();
                if (miss.Count > 0) parts.Add(parts.Count > 0 ? $"{Label(miss)} can be requested" : "Not available to stream");
                return string.Join(" · ", parts);
            }
        }

        /// <summary>"Season 3", "Seasons 1–2, 4".</summary>
        public static string Label(IEnumerable<int> numbers)
        {
            var ns = numbers.OrderBy(n => n).ToList();
            return $"{(ns.Count == 1 ? "Season" : "Seasons")} {Runs(ns)}";
        }

        public static string Runs(IEnumerable<int> numbers)
        {
            var runs = new List<(int A, int B)>();
            foreach (var n in numbers.OrderBy(n => n))
                if (runs.Count > 0 && n == runs[^1].B + 1) runs[^1] = (runs[^1].A, n); else runs.Add((n, n));
            return string.Join(", ", runs.Select(r => r.A == r.B ? $"{r.A}" : $"{r.A}–{r.B}"));
        }
    }

    public enum HitKind { Title, Channel, Listing, Recent }

    /// <summary>One search result, shown as a card. <see cref="Card"/> carries what playing it needs.</summary>
    public sealed record SearchHit(HitKind Kind, HomeCard Card, CatalogueTitle? Title = null, double Score = 0);

    /// <summary>Search results grouped as the design has them: top result, shows and films, channels, on TV.</summary>
    /// <remarks><see cref="Pending"/>: the library sources still searching (results arrive source by source).</remarks>
    public sealed record SearchResults(string Query, SearchHit? Top, IReadOnlyList<SearchHit> Titles, IReadOnlyList<SearchHit> Channels, IReadOnlyList<SearchHit> OnTv,
        IReadOnlyList<string>? PendingSources = null)
    {
        public IReadOnlyList<string> Pending => PendingSources ?? Array.Empty<string>();
        public static readonly SearchResults Empty = new("", null, Array.Empty<SearchHit>(), Array.Empty<SearchHit>(), Array.Empty<SearchHit>());
        public bool IsEmpty => Top == null && Titles.Count == 0 && Channels.Count == 0 && OnTv.Count == 0;
    }
}
