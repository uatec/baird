using System;
using System.Collections.Generic;
using Baird.Playback;
using Baird.Services;
using Baird.ViewModels;
using Microsoft.Extensions.Configuration;

namespace Baird.NewUi.Adapters
{
    /// <summary>
    /// Constructs the existing services for the new UI (as MainView does for the classic one, minus everything UI),
    /// including the shared browser behind Baird that plays Netflix, Disney+, ITVX and Channel 4.
    /// </summary>
    public sealed class LegacyServices
    {
        public TvHeadendService Tvh { get; }
        public IEpgService Epg => Tvh;
        public IDataService Data { get; }
        public IJellyseerrService Seerr { get; }
        /// <summary>For Settings' source rows (cheap state only).</summary>
        public JellyfinService Jellyfin { get; }
        public NetflixSession? Netflix { get; }
        public bool DisneyConfigured => Disney != null;
        /// <summary>Disney+ when it's set up (its credentials are in config.ini).</summary>
        public DisneyService? Disney { get; }
        public ICecService Cec { get; }
        public ScreensaverService Screensaver { get; } = new();
        public IReadOnlyDictionary<string, string> MpvOverrides { get; }
        /// <summary>The services that play in the browser behind Baird (only those that are set up).</summary>
        public IReadOnlyList<IBrowserPlaybackEngine> BrowserEngines { get; }

        public LegacyServices(IConfiguration config)
        {
            Tvh = new TvHeadendService(config);
            Jellyfin = new JellyfinService(config);
            var providers = new List<IMediaProvider> { Tvh, Jellyfin, new BbcIPlayerService(), new YouTubeService() };
            var engines = new List<IBrowserPlaybackEngine>();

            // One shared browser (one signed-in profile, one tab) for every browser service; one service at a time.
            var netflixCredentials = NetflixCredentials.FromConfiguration(config);
            var browser = new CdpBrowserService();
            browser.LogMessage += m => Console.WriteLine($"[NetflixBrowser] {m}");
            var netflix = new NetflixSession(browser, netflixCredentials, config["NETFLIX_PROFILE"] is { Length: > 0 } nfProfile ? nfProfile : null);
            netflix.Log += m => Console.WriteLine($"[NetflixSession] {m}");
            if (netflixCredentials != null)
            {
                Netflix = netflix;
                providers.Add(new NetflixProvider(netflix));
                netflix.Start(StartupDelays.Netflix);
                engines.Add(new NetflixPlaybackEngine(netflix, () => new NetflixPlaybackFlow(netflix.Browser, netflixCredentials) { LogBrowserToJournal = false }));
            }

            var disneyCredentials = DisneyCredentials.FromConfiguration(config);
            var disney = new DisneyService(netflix.Hub, disneyCredentials, config["DISNEY_PROFILE"] is { Length: > 0 } dnProfile ? dnProfile : null);
            disney.Log += m => Console.WriteLine($"[DisneyService] {m}");
            if (disneyCredentials != null)
            {
                Disney = disney;
                providers.Add(new DisneyProvider(disney));
                disney.Start(StartupDelays.Disney);
                engines.Add(new DisneyPlaybackEngine(disney, () => new DisneyPlaybackFlow(disney) { UseBairdHud = true }));
            }

            var channel4 = new Channel4Service(netflix.Hub, new Channel4Catalogue());
            channel4.Log += m => Console.WriteLine($"[Channel4Service] {m}");
            providers.Add(new Channel4Provider(channel4.Catalogue));
            engines.Add(new Channel4PlaybackEngine(channel4));

            var itvx = new ItvxService(netflix.Hub, new ItvxCatalogue(), ItvxCredentials.FromConfiguration(config));
            itvx.Log += m => Console.WriteLine($"[ItvxService] {m}");
            providers.Add(new ItvxProvider(itvx.Catalogue, itvx));
            engines.Add(new ItvxPlaybackEngine(itvx) { SkipAdverts = !string.Equals(config["Baird:ItvxSkipAdverts"], "false", StringComparison.OrdinalIgnoreCase) });

            BrowserEngines = engines;
            Data = new DataService(providers, new JsonHistoryService(), new JsonWatchlistService(), new MediaItemCache(), new JsonMediaDataCache());
            Seerr = new JellyseerrService(config);
            Cec = new CecService();
            var mpv = new Dictionary<string, string>();
            foreach (var kv in config.GetSection("Mpv").GetChildren()) if (kv.Value != null) mpv[kv.Key] = kv.Value;
            MpvOverrides = mpv;
        }
    }
}
