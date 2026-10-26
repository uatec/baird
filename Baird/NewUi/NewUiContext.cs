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
        /// <summary>What's on the channel being watched (refreshed when tuning), for the ident and the info bar.</summary>
        public Programme? Programme { get; set; }
        /// <summary>One search for everything (null until wired: Search then says so).</summary>
        public CatalogueSearch? Search { get; set; }
        /// <summary>Show pages' data and requests (null until wired: series cards then toast).</summary>
        public ShowData? Shows { get; set; }
        public ITimers Timers { get; set; } = new DispatcherTimers();

        public NewUiContext(Navigator nav, PlaybackController playback, HomeData home, Func<DateTimeOffset> now)
        {
            Nav = nav; Playback = playback; Home = home; Now = now;
        }

        private string PlaceLabel() => Nav.State.Pages.Count > 0 ? Nav.State.Pages[^1].Name : Nav.State.Place switch
        {
            Place.LiveTv => "Live TV",
            Place.Search => "Search",
            Place.Settings => "Settings",
            _ => "Home",
        };

        public void PlayLive(Channel channel)
        {
            var cur = Playback.Current;
            if (cur?.Channel?.Id == channel.Id && !Nav.State.AtVideo) { Nav.ToVideo(); return; }
            var from = PlaceLabel();
            Playback.PlayLive(channel);
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
            Nav.Play(new VideoRef("vod", card.Id));
            FlashBackChip(from);
        }

        /// <summary>OK on a card: play what can play; say plainly what will happen for the rest (never a dead end).</summary>
        public void Activate(HomeCard card)
        {
            if (card.Kind == CardKind.Channel && card.Channel != null) { PlayLive(card.Channel); return; }
            if (card.Via == PlayVia.Browser) { Toast($"{card.Source} plays in the new interface from phase 2. Switch to Classic in Settings to watch it now."); return; }
            if (card.Via == PlayVia.Mpv && card.StreamUrl != null && card.Kind != CardKind.Series) { PlayItem(card); return; }
            if (Shows != null) { OpenShow(ShowData.FromCard(card)); return; }
            Toast($"{card.Label}'s page isn't available.");
        }

        /// <summary>A show or film's page: seasons, episodes, where it streams, requests.</summary>
        public void OpenShow(CatalogueTitle title) => Nav.OpenPage(new PageRef("show", title));

        public Channel? NeighbourChannel(int step)
        {
            if (Channels.Count == 0) return null;
            var i = Channels.ToList().FindIndex(c => c.Id == Playback.Current?.Channel?.Id);
            return Channels[((i < 0 ? 0 : i + step) % Channels.Count + Channels.Count) % Channels.Count];
        }
    }
}
