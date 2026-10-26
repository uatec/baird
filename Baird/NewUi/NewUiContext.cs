using System;
using System.Collections.Generic;
using System.Linq;
using Baird.NewUi.Adapters;
using Baird.NewUi.Core;
using Baird.NewUi.Model;
using Baird.NewUi.Playback;

namespace Baird.NewUi
{
    /// <summary>What screens share: navigation, playback, data, and the shell's toast and back chip.</summary>
    public sealed class NewUiContext
    {
        public Navigator Nav { get; }
        public PlaybackController Playback { get; }
        public HomeData Home { get; }
        public Func<DateTimeOffset> Now { get; }
        public Action<string> Toast { get; set; } = _ => { };
        public Action<string> FlashBackChip { get; set; } = _ => { };
        /// <summary>Channels in number order (from On now), for Pg± and number entry.</summary>
        public IReadOnlyList<Channel> Channels { get; set; } = Array.Empty<Channel>();
        public event Action<Channel>? Tuning;
        /// <summary>Up next changed (an item was taken off it): Home refreshes the row.</summary>
        public event Action? UpNextChanged;
        public void RaiseUpNextChanged() => UpNextChanged?.Invoke();
        /// <summary>The episodes of the show being watched, in order (set by the show page), for "Up next".</summary>
        public IReadOnlyList<HomeCard> Following { get; set; } = Array.Empty<HomeCard>();
        /// <summary>The show those episodes belong to ("Up next in …", "Back to …"), and each episode's title and
        /// "Season 1, episode 2 · 45 min" by card id.</summary>
        public string? FollowingShow { get; set; }
        /// <summary>Finds an episode's show and its episodes in order (for Up next when it wasn't started from the show page).</summary>
        public Func<HomeCard, System.Threading.Tasks.Task<(IReadOnlyList<HomeCard> Episodes, string Show)?>>? FindFollowing { get; set; }
        public IReadOnlyDictionary<string, (string Title, string Meta)> FollowingInfo { get; set; } = new Dictionary<string, (string, string)>();
        /// <summary>Settings: play the next episode by itself after a countdown (off: the end card waits for OK).</summary>
        public bool Autoplay { get; set; } = true;
        private async System.Threading.Tasks.Task FindFollowingAsync(HomeCard card)
        {
            try
            {
                if (await FindFollowing!(card) is not { } found || !found.Episodes.Any(c => c.Id == card.Id)) return;
                if (Playback.Current?.ItemId != card.Id) return; // moved on meanwhile
                Following = found.Episodes;
                FollowingShow = found.Show;
            }
            catch (Exception ex) { Console.WriteLine($"[NewUi] Couldn't find {card.Label}'s show: {ex.Message}"); }
        }

        public HomeCard? NextEpisode()
        {
            var id = Playback.Current?.ItemId;
            var i = Following.ToList().FindIndex(c => c.Id == id);
            return i >= 0 && i + 1 < Following.Count ? Following[i + 1] : null;
        }
        /// <summary>An on-demand item started (the shell shows its loading card).</summary>
        public event Action<HomeCard>? Starting;
        /// <summary>OK on the video answers the service's prompt ("Skip intro") if one is up.</summary>
        /// <summary>Next audio track (mpv); returns a label for it, or null when there's no choice.</summary>
        public Func<string?> CycleAudio { get; set; } = () => null;
        public Func<bool> TryActivatePrompt { get; set; } = () => false;
        /// <summary>What OK does about the current failure ("sign in to Channel 4"), if anything.</summary>
        public Func<string?> FailureAction { get; set; } = () => null;
        /// <summary>Does it: null when the title can be played again now, else why not.</summary>
        public Func<System.Threading.Tasks.Task<string?>> RunFailureAction { get; set; } = () => System.Threading.Tasks.Task.FromResult<string?>(null);
        /// <summary>What's on the channel being watched (refreshed when tuning), for the ident and the info bar.</summary>
        public Programme? Programme { get; set; }
        /// <summary>One search for everything (null until wired: Search then says so).</summary>
        public CatalogueSearch? Search { get; set; }
        /// <summary>Show pages' data and requests (null until wired: series cards then toast).</summary>
        public ShowData? Shows { get; set; }
        public ITimers Timers { get; set; } = new DispatcherTimers();
        /// <summary>The viewer's reminders (in memory unless the composition root gives it a file).</summary>
        public Reminders Reminders { get; set; }

        /// <summary>The guide's layout (Settings and the guide's strip both switch it).</summary>
        public GuideLayout GuideLayout { get; private set; }
        /// <summary>Persists the guide layout (the composition root writes Baird:GuideLayout; tests leave it a no-op).</summary>
        public Action<GuideLayout> SaveGuideLayout { get; set; } = _ => { };
        public event Action? GuideLayoutChanged;

        public void SetGuideLayout(GuideLayout layout, bool save = true)
        {
            if (GuideLayout == layout) return;
            GuideLayout = layout;
            if (save) SaveGuideLayout(layout);
            GuideLayoutChanged?.Invoke();
        }

        public NewUiContext(Navigator nav, PlaybackController playback, HomeData home, Func<DateTimeOffset> now)
        {
            Nav = nav; Playback = playback; Home = home; Now = now;
            Reminders = new Reminders(null, now);
        }

        private string PlaceLabel() => Nav.State.Pages.Count > 0 ? Nav.State.Pages[^1].Label : Nav.State.Place switch
        {
            Place.LiveTv => "Live TV",
            Place.Search => "Search",
            Place.Settings => "Settings",
            _ => "Home",
        };

        private HomeCard? _nowPlaying;

        /// <summary>What's playing, as a card (badge "playing"), for the front of Home's first row; null when nothing plays.</summary>
        public HomeCard? NowPlaying
        {
            get
            {
                var cur = Playback.Current;
                if (cur == null || _nowPlaying == null) return null;
                if (cur.Kind == "live" ? _nowPlaying.Channel?.Id != cur.Channel?.Id : _nowPlaying.Id != cur.ItemId) return null;
                return _nowPlaying.Kind == CardKind.Channel ? _nowPlaying with { Programme = Programme ?? _nowPlaying.Programme, Sublabel = Programme?.Title ?? _nowPlaying.Sublabel } : _nowPlaying;
            }
        }

        public void PlayLive(Channel channel)
        {
            var cur = Playback.Current;
            if (cur?.Channel?.Id == channel.Id && !Nav.State.AtVideo) { Nav.ToVideo(); return; }
            var from = PlaceLabel();
            Playback.PlayLive(channel);
            _nowPlaying = new HomeCard(CardKind.Channel, channel.Id, $"{channel.Number}  {channel.Name}", null, channel.LogoUrl, null, "playing",
                "TV", PlayVia.Mpv, channel.StreamUrl, Channel: channel);
            Tuning?.Invoke(channel);
            Nav.Play(new VideoRef("live", channel.Id), keepOverlay: Nav.State.AtVideo && Nav.State.Overlay != null);
            if (!Nav.State.AtVideo || Nav.State.Overlay == null) FlashBackChip(from);
        }

        public void PlayItem(HomeCard card)
        {
            var cur = Playback.Current;
            if (cur?.ItemId == card.Id && !Nav.State.AtVideo) { Nav.ToVideo(); return; }
            var from = PlaceLabel();
            Playback.PlayItem(card.Id, card.StreamUrl!, card.Resume, card.Label);
            _nowPlaying = card with { Badge = "playing", Progress = null };
            if (card.Kind == CardKind.Episode && !Following.Any(c => c.Id == card.Id) && FindFollowing != null) _ = FindFollowingAsync(card);
            Starting?.Invoke(card);
            Nav.Play(new VideoRef("vod", card.Id));
            FlashBackChip(from);
        }

        /// <summary>OK on a card: play what can play; say plainly what will happen for the rest (never a dead end).</summary>
        public void Activate(HomeCard card)
        {
            if (card.Kind == CardKind.Channel && card.Channel != null) { PlayLive(card.Channel); return; }
            if (card.Via != PlayVia.None && card.StreamUrl != null && card.Kind != CardKind.Series) { PlayItem(card); return; }
            if (Shows != null) { OpenShow(ShowData.FromCard(card)); return; }
            Toast($"{card.Label}'s page isn't available.");
        }

        /// <summary>A show or film's page: seasons, episodes, where it streams, requests.</summary>
        public void OpenShow(CatalogueTitle title) => Nav.OpenPage(new PageRef("show", title, title.Name));

        /// <summary>The guide, lit on a channel at a time (a programme, so a reminder can be set). Wired by the shell.</summary>
        public Action<string, DateTimeOffset> OpenGuide { get; set; } = (_, _) => { };

        public Channel? NeighbourChannel(int step)
        {
            if (Channels.Count == 0) return null;
            var i = Channels.ToList().FindIndex(c => c.Id == Playback.Current?.Channel?.Id);
            return Channels[((i < 0 ? 0 : i + step) % Channels.Count + Channels.Count) % Channels.Count];
        }
    }
}
