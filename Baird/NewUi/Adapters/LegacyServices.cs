using System;
using System.Collections.Generic;
using Baird.Services;
using Microsoft.Extensions.Configuration;

namespace Baird.NewUi.Adapters
{
    /// <summary>
    /// Constructs the existing services for the new UI (as MainView does for the classic one, minus everything UI).
    /// Netflix and Disney+ arrive in phase 2: their engines are still tied to the classic spike screens.
    /// </summary>
    public sealed class LegacyServices
    {
        public TvHeadendService Tvh { get; }
        public IEpgService Epg => Tvh;
        public IDataService Data { get; }
        public IJellyseerrService Seerr { get; }
        public ICecService Cec { get; }
        public IReadOnlyDictionary<string, string> MpvOverrides { get; }

        public LegacyServices(IConfiguration config)
        {
            Tvh = new TvHeadendService(config);
            var providers = new List<IMediaProvider> { Tvh, new JellyfinService(config), new BbcIPlayerService(), new YouTubeService() };
            Data = new DataService(providers, new JsonHistoryService(), new JsonWatchlistService(), new MediaItemCache(), new JsonMediaDataCache());
            Seerr = new JellyseerrService(config);
            Cec = new CecService();
            var mpv = new Dictionary<string, string>();
            foreach (var kv in config.GetSection("Mpv").GetChildren()) if (kv.Value != null) mpv[kv.Key] = kv.Value;
            MpvOverrides = mpv;
        }
    }
}
