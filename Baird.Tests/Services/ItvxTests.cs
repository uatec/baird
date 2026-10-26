using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Baird.Models;
using Baird.Mpv;
using Baird.Services;
using Xunit;

namespace Baird.Tests.Services
{
    /// <summary>Shapes copied from ITVX's search API on 2026-10-26 (trimmed): a programme, a special with and without a parent
    /// programme, a film, a Premium (PAID) programme and a live channel.</summary>
    internal static class ItvxFixtures
    {
        public const string SearchReply = @"{""results"":[
            {""id"":""mz0gq64-itv_hub-dotcom-ITV-mpeg-dash-widevine"",""entityType"":""programme"",""contentType"":""vod"",""data"":{
                ""programmeCCId"":""mz0gq64"",""legacyId"":{""officialFormat"":""1/7314"",""apiEncoded"":""1_7314""},""productionId"":""1/7314/0062#001"",
                ""programmeTitle"":""Vera"",""synopsis"":""Brenda Blethyn is the obsessive crime-cracker DCI Vera Stanhope"",""genre"":[""DRAMA""],
                ""latestAvailableEpisode"":{""episodeCCId"":""vdhl3l1"",""imageHref"":""https://ovp.itv.com/images/programme/mz0gq64/itv_hub/04_DesktopCTV_RailTileLandscape/16x9?distributionPartner=itv_hub&treatment=title&w={width}&h={height}&q={quality}&blur={blur}&bg={bg}&fallback=standard""},
                ""totalAvailableEpisodes"":56,""tier"":""FREE""}},
            {""id"":""3wczy52-itv_hub-dotcom-ITV-mpeg-dash-outband-webvtt-widevine"",""entityType"":""special"",""contentType"":""vod"",""data"":{
                ""specialCCId"":""3wczy52"",""productionId"":""1/5260/0215#001"",""specialTitle"":""A Very Special Guest"",
                ""specialProgramme"":{""programmeCCId"":""d5ncvxk"",""programmeTitle"":""Pocoyo""},
                ""synopsis"":""It’s Christmas and there's an unexpected guest at Elly's house."",
                ""imageHref"":""https://ovp.itv.com/images/special/3wczy52/itv_hub/04_DesktopCTV_RailTileLandscape/16x9?distributionPartner=itv_hub&treatment=title&w={width}&h={height}&q={quality}&blur={blur}&bg={bg}&fallback=standard"",
                ""tier"":""FREE""}},
            {""id"":""dy6cs7y-itv_hub-dotcom-ITV-fairplay-hls"",""entityType"":""special"",""contentType"":""vod"",""data"":{
                ""specialCCId"":""dy6cs7y"",""productionId"":""10/5961/0001#001"",""specialTitle"":""Vera, Farewell Pet"",
                ""synopsis"":""End of a (V)era! Brenda Blethyn reflects on 14 years as the iconic cop"",
                ""imageHref"":""https://ovp.itv.com/images/special/dy6cs7y/itv_hub/04_DesktopCTV_RailTileLandscape/16x9?distributionPartner=itv_hub&treatment=title&w={width}&h={height}&q={quality}&blur={blur}&bg={bg}&fallback=standard"",
                ""tier"":""FREE""}},
            {""id"":""bdgqfy6-itv_hub-dotcom-ITV-mpeg-dash-widevine"",""entityType"":""film"",""contentType"":""vod"",""data"":{
                ""filmCCId"":""bdgqfy6"",""productionId"":""10/2713/0001#001"",""filmTitle"":""Mr Christmas"",
                ""synopsis"":""Perfect presents, accidental love & rocky friendships - an enticing tale"",
                ""imageHref"":""https://ovp.itv.com/images/film/bdgqfy6/itv_hub/04_DesktopCTV_RailTileLandscape/16x9?distributionPartner=itv_hub&treatment=title&w={width}&h={height}&q={quality}&blur={blur}&bg={bg}&fallback=standard"",
                ""duration"":""PT1H23M"",""tier"":""FREE""}},
            {""id"":""f1lr1y1-itv_hub-dotcom-ITV-hd-mpeg-dash-widevine"",""entityType"":""programme"",""contentType"":""vod"",""data"":{
                ""programmeCCId"":""f1lr1y1"",""programmeTitle"":""Miss Marple"",""synopsis"":""Joan Hickson is Christie’s sharp-eyed sleuth"",
                ""latestAvailableEpisode"":{""episodeCCId"":""5qdn6w8"",""imageHref"":""https://ovp.itv.com/images/programme/f1lr1y1/x?w={width}""},
                ""totalAvailableEpisodes"":22,""tier"":""PAID""}},
            {""id"":""itv2-simulcast"",""entityType"":""simulcast"",""channelType"":""simulcast"",""data"":{""channel"":""ITV2"",""tier"":""FREE""}}
        ]}";
    }

    /// <summary>Programme page data as <see cref="ItvxPlayerControl.ProgrammeFetchScript"/> returns it, trimmed from Vera's and Mr Christmas'
    /// pages on the Pi (2026-10-26), plus a Premium episode and a series with nothing but Premium.</summary>
    internal static class ItvxPageFixtures
    {
        public const string Vera = @"{""programme"":{""ccid"":""mz0gq64"",""title"":""Vera"",""titleSlug"":""vera"",""tier"":[""FREE""]},""seriesList"":[
            {""seriesNumber"":""1"",""seriesLabel"":""Series 1"",""titles"":[
              {""ccid"":""zz20f0t"",""episode"":1,""episodeTitle"":""Hidden Depths"",""description"":""DCI Vera Stanhope investigates the similar killings of two young people."",""notFormattedDuration"":""PT1H29M"",""image"":""https://ovp.itv.com/v2/images/episode/zz20f0t/itv_hub/08_Episodic/16x9?distributionPartner=itv_hub&fallback=standard&w={width}&h={height}&q={quality}&blur={blur}&bg={bg}"",""premium"":false,""tier"":[""FREE""],""encodedEpisodeId"":{""letterA"":""1a7314a0001"",""underscore"":""1_7314_0001""}},
              {""ccid"":""3vmbwxp"",""episode"":2,""episodeTitle"":""Telling Tales"",""description"":""After a woman dies by suicide, evidence comes to light."",""notFormattedDuration"":""PT1H29M"",""image"":""https://ovp.itv.com/v2/images/episode/3vmbwxp/x?w={width}"",""premium"":false,""tier"":[""FREE""],""encodedEpisodeId"":{""letterA"":""1a7314a0002""}},
              {""ccid"":""prem001"",""episode"":3,""episodeTitle"":""Premium Only"",""notFormattedDuration"":""PT1H29M"",""premium"":true,""tier"":[""PAID""],""encodedEpisodeId"":{""letterA"":""1a7314a0003""}}
            ]},
            {""seriesNumber"":""2"",""seriesLabel"":""Series 2"",""titles"":[
              {""ccid"":""00ddsx5"",""episode"":1,""episodeTitle"":""The Ghost Position"",""description"":""Vera's reunion with her first-ever sergeant."",""notFormattedDuration"":""PT1H30M"",""image"":null,""premium"":false,""tier"":[""FREE""],""encodedEpisodeId"":{""letterA"":""1a7314a0007""}}
            ]},
            {""seriesNumber"":""3"",""seriesLabel"":""Series 3"",""titles"":[
              {""ccid"":""prem002"",""episode"":1,""episodeTitle"":""Also Premium"",""premium"":false,""tier"":[""PAID""],""encodedEpisodeId"":{""letterA"":""1a7314a0010""}}
            ]}
        ]}";

        public const string Film = @"{""programme"":{""ccid"":""bdgqfy6"",""title"":""Mr Christmas"",""titleSlug"":""mr-christmas"",""tier"":[""FREE""]},""seriesList"":[
            {""seriesNumber"":""others"",""seriesLabel"":""Other Episodes"",""titles"":[
              {""ccid"":""bdgqfy6"",""episode"":null,""episodeTitle"":""Mr Christmas"",""description"":""Perfect presents"",""notFormattedDuration"":""PT1H23M"",""premium"":false,""tier"":[""FREE""],""encodedEpisodeId"":{""letterA"":""10a2713a0001""}}
            ]}
        ]}";
    }

    public class ItvxCatalogueTests
    {
        [Fact]
        public void A_programme_page_gives_its_free_episodes_by_series_with_labels_lengths_images_and_play_urls()
        {
            var p = ItvxCatalogue.ParseProgramme(ItvxPageFixtures.Vera, out var failure);

            Assert.Null(failure);
            Assert.NotNull(p);
            Assert.Equal("vera", p!.Slug);
            Assert.Equal("mz0gq64", p.Ccid);
            // Series 3 has nothing but Premium, so it is not offered.
            Assert.Equal(new[] { ("1", "Series 1"), ("2", "Series 2") }, p.Series);
            Assert.Equal(new[] { "Hidden Depths", "Telling Tales", "The Ghost Position" }, p.Episodes.Select(e => e.Title));
            var e1 = p.Episodes[0];
            Assert.Equal("https://www.itv.com/watch/vera/mz0gq64/1a7314a0001", e1.PlayUrl);
            Assert.Equal("S1:E1", e1.Label);
            Assert.Equal("zz20f0t", e1.Ccid);
            Assert.Equal(89 * 60, e1.RuntimeSeconds);
            Assert.Equal("https://ovp.itv.com/v2/images/episode/zz20f0t/itv_hub/08_Episodic/16x9?distributionPartner=itv_hub&fallback=standard&w=480&h=270&q=80&blur=0&bg=false", e1.ImageUrl);
            Assert.Null(p.Episodes[2].ImageUrl);
            Assert.Equal("S2:E1", p.Episodes[2].Label);
        }

        [Fact]
        public void A_film_page_has_one_item_and_no_episode_label()
        {
            var p = ItvxCatalogue.ParseProgramme(ItvxPageFixtures.Film, out _)!;

            Assert.Single(p.Episodes);
            Assert.Equal("", p.Episodes[0].Label);
            Assert.Equal("https://www.itv.com/watch/mr-christmas/bdgqfy6/10a2713a0001", p.Episodes[0].PlayUrl);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("{oops")]
        [InlineData(@"{""programme"":{}}")]
        public void Page_data_without_a_programme_gives_a_reason(string? json)
        {
            Assert.Null(ItvxCatalogue.ParseProgramme(json, out var failure));
            Assert.False(string.IsNullOrEmpty(failure));
        }

        [Theory]
        [InlineData("https://www.itv.com/watch/vera/mz0gq64/1a7314a0001", "vera", "mz0gq64", "1a7314a0001")]
        [InlineData("https://www.itv.com/watch/mr-christmas/bdgqfy6", "mr-christmas", "bdgqfy6", null)]
        public void A_watch_url_gives_its_slug_and_ids(string url, string slug, string id, string? episode)
        {
            Assert.True(ItvxCatalogue.TryParseWatchUrl(url, out var s, out var i, out var e));
            Assert.Equal((slug, id, episode), (s, i, e));
        }

        [Fact]
        public void Search_lists_free_programmes_specials_and_films_and_drops_premium_and_live_channels()
        {
            var results = ItvxCatalogue.ParseSearch(ItvxFixtures.SearchReply)!;

            Assert.Equal(new[] { "Vera", "A Very Special Guest", "Vera, Farewell Pet", "Mr Christmas" }, results.Select(r => r.Title));
            Assert.DoesNotContain(results, r => r.Title == "Miss Marple"); // ITVX Premium (tier PAID)
        }

        [Fact]
        public void A_programme_opens_its_page_and_shows_its_latest_episode_image()
        {
            var vera = ItvxCatalogue.ParseSearch(ItvxFixtures.SearchReply)![0];

            Assert.Equal(ItvxKind.Programme, vera.Kind);
            Assert.Equal("mz0gq64", vera.Id);
            Assert.Equal("vera", vera.Slug);
            Assert.Equal(56, vera.EpisodeCount);
            Assert.Equal("https://www.itv.com/watch/vera/mz0gq64", vera.WatchUrl);
            Assert.Equal("https://ovp.itv.com/images/programme/mz0gq64/itv_hub/04_DesktopCTV_RailTileLandscape/16x9?distributionPartner=itv_hub&treatment=title&w=480&h=270&q=80&blur=0&bg=false&fallback=standard",
                vera.ImageUrl);
            Assert.Equal("Brenda Blethyn is the obsessive crime-cracker DCI Vera Stanhope", vera.Description);
        }

        [Fact]
        public void A_special_plays_from_its_own_page_even_when_it_has_a_parent_programme()
        {
            var results = ItvxCatalogue.ParseSearch(ItvxFixtures.SearchReply)!;

            Assert.Equal(ItvxKind.Special, results[1].Kind);
            Assert.Equal("Pocoyo", results[1].ParentTitle);
            // Measured on the Pi: /watch/pocoyo/d5ncvxk/3wczy52 falls back to Pocoyo's page; the special's own page plays it.
            Assert.Equal("https://www.itv.com/watch/a-very-special-guest/3wczy52", results[1].WatchUrl);
            Assert.Equal("https://www.itv.com/watch/vera-farewell-pet/dy6cs7y", results[2].WatchUrl);
        }

        [Fact]
        public void A_film_plays_from_its_own_page_with_its_length()
        {
            var film = ItvxCatalogue.ParseSearch(ItvxFixtures.SearchReply)![3];

            Assert.Equal(ItvxKind.Film, film.Kind);
            Assert.Equal("https://www.itv.com/watch/mr-christmas/bdgqfy6", film.WatchUrl);
            Assert.Equal(83 * 60, film.RuntimeSeconds);
        }

        [Theory]
        [InlineData("Vera", "vera")]
        [InlineData("Hell's Kitchen", "hells-kitchen")]
        [InlineData("Paddington’s Birthday Bonanza", "paddingtons-birthday-bonanza")]
        [InlineData("Ant & Dec's Saturday Night Takeaway", "ant-and-decs-saturday-night-takeaway")]
        [InlineData("Love Island: Aftersun", "love-island-aftersun")]
        [InlineData("Mr Bates vs The Post Office", "mr-bates-vs-the-post-office")]
        [InlineData("The Chase - Celebrity Special", "the-chase-celebrity-special")]
        public void A_title_becomes_the_slug_in_its_watch_url(string title, string slug) => Assert.Equal(slug, ItvxCatalogue.Slug(title));

        [Theory]
        [InlineData("PT1H23M", 4980)]
        [InlineData("PT46M", 2760)]
        [InlineData("PT45M30S", 2730)]
        [InlineData("PT2H", 7200)]
        [InlineData("", 0)]
        [InlineData(null, 0)]
        [InlineData("soon", 0)]
        public void An_iso_duration_is_read_in_seconds(string? iso, double seconds) => Assert.Equal(seconds, ItvxCatalogue.ParseIsoDuration(iso));

        [Theory]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData(@"{""error"":""x""}")]
        [InlineData(@"[1,2]")]
        public void A_reply_that_is_not_search_results_is_null(string body) => Assert.Null(ItvxCatalogue.ParseSearch(body));

        [Theory]
        [InlineData("https://www.itv.com/watch/vera/mz0gq64", true)]
        [InlineData("https://www.itv.com/watch/pocoyo/d5ncvxk/3wczy52", true)]
        [InlineData("https://itv.com/watch/vera/mz0gq64?x=1", true)]
        [InlineData("https://www.itv.com/watch/collections/drama", false)]
        [InlineData("https://www.channel4.com/programmes/derry-girls", false)]
        [InlineData(null, false)]
        public void Watch_urls_are_recognised(string? url, bool isItvx) => Assert.Equal(isItvx, ItvxCatalogue.IsItvxUrl(url));
    }

    internal sealed class FakeItvxHttp : HttpMessageHandler
    {
        public HttpStatusCode SearchStatus { get; set; } = HttpStatusCode.OK;
        public string? LastUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(SearchStatus) { Content = new StringContent(ItvxFixtures.SearchReply) });
        }

        public ItvxCatalogue Catalogue() => new(new HttpClient(this));
    }

    public class ItvxProviderTests
    {
        [Fact]
        public async Task Search_lists_a_programme_as_a_container_and_films_and_specials_as_playable()
        {
            var http = new FakeItvxHttp();
            var provider = new ItvxProvider(http.Catalogue());

            var items = (await provider.SearchAsync(" vera ")).ToList();

            Assert.Contains("query=vera", http.LastUrl);
            Assert.All(items, i => Assert.Equal("ITVX", i.Source));
            var vera = items[0];
            Assert.Equal("itvx:vera/mz0gq64", vera.Id);
            Assert.Equal(MediaType.Brand, vera.Type);
            Assert.Equal("", vera.StreamUrl);
            Assert.Equal("56 Episodes", vera.Details);

            var film = items.Single(i => i.Name == "Mr Christmas");
            Assert.Equal(MediaType.Video, film.Type);
            Assert.Equal("itvx:ep:bdgqfy6", film.Id);
            Assert.Equal("https://www.itv.com/watch/mr-christmas/bdgqfy6", film.StreamUrl);
            Assert.Equal("Film", film.Details);
            Assert.Equal(TimeSpan.FromMinutes(83), film.Duration);

            var special = items.Single(i => i.Name == "A Very Special Guest");
            Assert.Equal(MediaType.Video, special.Type);
            Assert.Equal("Pocoyo", special.Details);
            Assert.DoesNotContain(items, i => i.Name == "Miss Marple");
        }

        [Fact]
        public async Task A_blank_query_does_not_search()
        {
            var http = new FakeItvxHttp();
            Assert.Empty(await new ItvxProvider(http.Catalogue()).SearchAsync("  "));
            Assert.Null(http.LastUrl);
        }

        [Fact]
        public async Task A_failed_search_is_loud()
        {
            var provider = new ItvxProvider(new FakeItvxHttp { SearchStatus = HttpStatusCode.ServiceUnavailable }.Catalogue());
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => provider.SearchAsync("vera"));
            Assert.Contains("503", ex.Message);
        }
    }

    /// <summary>A shared browser showing ITVX: page states in order, then the last one for ever.</summary>
    internal sealed class FakeItvxBrowser : Baird.Tests.ViewModels.MockCdpBrowserService, ICdpPageDriver
    {
        public Queue<JsonObject> States { get; } = new();
        public JsonObject LastState { get; set; } = State("https://www.itv.com/robots.txt");
        /// <summary>(selector, text) of every field typed into.</summary>
        public List<(string Selector, string Text)> Typed { get; } = new();
        /// <summary>What was pressed or sent, in order: "play", "parental", "use-password", "button:continue", "fetch:/watch/...", "control".</summary>
        public List<string> Actions { get; } = new();
        public List<string> ControlScripts { get; } = new();
        public bool StyleApplied { get; private set; }
        /// <summary>What a programme-page fetch returns (a JSON string, or an object with "error").</summary>
        public JsonNode? FetchReply { get; set; } = JsonValue.Create(ItvxPageFixtures.Vera);

        public static JsonObject State(string url, bool signInPage = false, bool signedIn = false, bool email = false, bool password = false,
            bool playButton = false, bool parental = false, bool video = false, double t = 0, double duration = 0, bool paused = false,
            bool ended = false, bool advert = false, double advertTime = 0, string text = "") => new()
            {
                ["url"] = url,
                ["signInPage"] = signInPage,
                ["signedIn"] = signedIn,
                ["emailField"] = email,
                ["passwordField"] = password,
                ["playButton"] = playButton,
                ["parentalPrompt"] = parental,
                ["hasVideo"] = video,
                ["currentTime"] = t,
                ["duration"] = duration,
                ["paused"] = paused,
                ["ended"] = ended,
                ["advertPlaying"] = advert,
                ["advertTime"] = advertTime,
                ["text"] = text,
            };

        public Task<JsonElement?> EvaluateAsync(string script, CancellationToken ct = default)
        {
            string json;
            if (script.Contains("signInPage:")) json = (States.Count > 0 ? States.Dequeue() : LastState).ToJsonString();
            else if (script.Contains("__bairdItvxStyle")) { StyleApplied = true; json = "true"; }
            else if (script == ItvxPlayerControl.PressPlayScript) { Actions.Add("play"); json = "\"clicked\""; }
            else if (script == ItvxPlayerControl.AcceptParentalScript) { Actions.Add("parental"); json = "\"clicked\""; }
            else if (script == ItvxPlayerControl.UsePasswordScript) { Actions.Add("use-password"); json = "\"clicked\""; }
            else if (script.Contains("labels.includes"))
            {
                Actions.Add("button:" + (script.Contains("\"sign in\"") ? "sign in" : script.Contains("\"continue\"") ? "continue" : "?"));
                json = "\"clicked\"";
            }
            else if (script.Contains("DOMParser"))
            {
                Actions.Add("fetch:" + script[(script.LastIndexOf("})(\"", StringComparison.Ordinal) + 4)..].TrimEnd(')', '"'));
                json = FetchReply?.ToJsonString() ?? "null";
            }
            else { ControlScripts.Add(script); Actions.Add("control"); json = "\"video\""; }
            return Task.FromResult<JsonElement?>(JsonDocument.Parse(json).RootElement.Clone());
        }

        public Task<bool> TypeIntoAsync(string selector, string text, CancellationToken ct = default) { Typed.Add((selector, text)); return Task.FromResult(true); }
        public Task PressEnterAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveMouseAsync(int x, int y, CancellationToken ct = default) => Task.CompletedTask;
        public Task<CapturedRequest?> NavigateAndCaptureAsync(string url, Func<CapturedRequest, bool> match, TimeSpan timeout, CancellationToken ct = default) =>
            Task.FromResult<CapturedRequest?>(null);
    }

    public class ItvxPlaybackEngineTests
    {
        internal const string Episode = "https://www.itv.com/watch/vera/mz0gq64/1a7314a0001";
        private static readonly ItvxCredentials Credentials = new("viewer@example.com", "s3cret-pass");

        internal static (FakeItvxBrowser Browser, ItvxService Service, ItvxPlaybackEngine Engine) Engine(ItvxCredentials? credentials = null, bool noCredentials = false)
        {
            var b = new FakeItvxBrowser { IsRunning = true };
            var service = new ItvxService(new BrowserHub(b), new FakeItvxHttp().Catalogue(), noCredentials ? null : credentials ?? Credentials)
            {
                PollInterval = TimeSpan.FromMilliseconds(2),
                SignInTimeout = TimeSpan.FromSeconds(1),
                UsePasswordRetry = TimeSpan.FromMilliseconds(20),
            };
            var engine = new ItvxPlaybackEngine(service)
            {
                PollInterval = TimeSpan.FromHours(1), // tests poll by hand
                StartTimeout = TimeSpan.FromSeconds(2),
            };
            return (b, service, engine);
        }

        /// <summary>The title page with its Play button, then the programme playing once it is pressed.</summary>
        internal static void PlaysAfterPress(FakeItvxBrowser b, string url = Episode, double duration = 5360)
        {
            b.States.Enqueue(FakeItvxBrowser.State(url, signedIn: true, playButton: true));
            b.States.Enqueue(FakeItvxBrowser.State(url, signedIn: true, video: true, t: 0, duration: duration));
            b.States.Enqueue(FakeItvxBrowser.State(url, signedIn: true, video: true, t: 0.5, duration: duration));
            b.LastState = FakeItvxBrowser.State(url, signedIn: true, video: true, t: 1.0, duration: duration);
        }

        [Fact]
        public async Task A_title_plays_after_its_Play_button_is_pressed_with_the_page_hidden_and_the_tab_marked_as_playing()
        {
            var (b, service, engine) = Engine();
            PlaysAfterPress(b);
            var loaded = 0;
            engine.FileLoaded += (_, _) => loaded++;

            await engine.PlayAsync(Episode);

            Assert.Equal(PlaybackState.Playing, engine.State);
            Assert.Equal(1, loaded);
            Assert.Equal(Episode, b.NavigatedUrls.Single());
            Assert.Equal(new[] { "play" }, b.Actions);
            Assert.True(b.StyleApplied);
            Assert.Equal("ITVX", service.Hub.ActivePlayback);
            Assert.Equal("ITVX", service.Hub.PageOwner);
            Assert.Empty(b.Typed);

            await engine.PollOnceAsync();
            Assert.Equal(5360, engine.DurationSeconds);
            Assert.Equal(1.0, engine.TimePositionSeconds);
        }

        [Fact]
        public async Task The_parental_controls_prompt_is_answered()
        {
            var (b, _, engine) = Engine();
            b.States.Enqueue(FakeItvxBrowser.State(Episode, signedIn: true, playButton: true));
            b.States.Enqueue(FakeItvxBrowser.State(Episode, signedIn: true, playButton: true, parental: true));
            b.States.Enqueue(FakeItvxBrowser.State(Episode, signedIn: true, video: true, t: 0.2, duration: 5360));
            b.LastState = FakeItvxBrowser.State(Episode, signedIn: true, video: true, t: 0.7, duration: 5360);

            await engine.PlayAsync(Episode);

            Assert.Equal(PlaybackState.Playing, engine.State);
            Assert.Equal(new[] { "play", "parental" }, b.Actions);
        }

        [Fact]
        public async Task Play_is_pressed_again_after_the_parental_prompt_when_the_page_does_not_start()
        {
            var (b, _, engine) = Engine();
            engine.PlayPressGap = TimeSpan.FromMilliseconds(30);
            var page = FakeItvxBrowser.State(Episode, signedIn: true, playButton: true);
            for (var i = 0; i < 3; i++) b.States.Enqueue(page);                      // early presses do nothing
            b.States.Enqueue(FakeItvxBrowser.State(Episode, signedIn: true, playButton: true, parental: true));
            for (var i = 0; i < 40; i++) b.States.Enqueue(page);                     // answered, but the page did not start
            b.States.Enqueue(FakeItvxBrowser.State(Episode, signedIn: true, video: true, t: 0.2, duration: 5360));
            b.LastState = FakeItvxBrowser.State(Episode, signedIn: true, video: true, t: 0.7, duration: 5360);

            await engine.PlayAsync(Episode);

            Assert.Equal(PlaybackState.Playing, engine.State);
            var parental = b.Actions.IndexOf("parental");
            Assert.True(parental >= 0);
            Assert.Contains("play", b.Actions.Skip(parental)); // pressed again after the prompt
        }

        [Fact]
        public async Task An_advert_counts_as_playing_and_leaves_the_position_alone()
        {
            var (b, _, engine) = Engine();
            b.States.Enqueue(FakeItvxBrowser.State(Episode, signedIn: true, playButton: true));
            b.States.Enqueue(FakeItvxBrowser.State(Episode, signedIn: true, video: true, paused: true, duration: 4990.8, advert: true, advertTime: 0.3));
            b.States.Enqueue(FakeItvxBrowser.State(Episode, signedIn: true, video: true, paused: true, duration: 4990.8, advert: true, advertTime: 0.8));
            b.LastState = FakeItvxBrowser.State(Episode, signedIn: true, video: true, paused: true, duration: 4990.8, advert: true, advertTime: 1.3);

            await engine.PlayAsync(Episode);
            Assert.True(engine.IsAdvertPlaying); // known as soon as it plays, before the first poll
            Assert.Equal(4990.8, engine.DurationSeconds);
            await engine.PollOnceAsync();

            Assert.Equal(PlaybackState.Playing, engine.State); // an advert plays: not paused
            Assert.True(engine.IsAdvertPlaying);
            Assert.Equal(0, engine.TimePositionSeconds);
            Assert.Equal(4990.8, engine.DurationSeconds); // the programme's length, read from its own video

            engine.SeekRelative(30); // adverts cannot be skipped
            await engine.WhenCommandsDoneAsync();
            Assert.Empty(b.ControlScripts);
        }

        [Fact]
        public async Task A_sign_in_page_signs_in_with_the_password_not_an_emailed_code_then_plays_the_title_again()
        {
            var (b, service, engine) = Engine();
            const string signIn = ItvxService.SignInUrl;
            b.States.Enqueue(FakeItvxBrowser.State(Episode, playButton: true));                              // signed out
            b.States.Enqueue(FakeItvxBrowser.State(signIn, signInPage: true, email: true));                  // Play went to the sign-in
            b.States.Enqueue(FakeItvxBrowser.State(signIn, signInPage: true, email: true));                  // the service's sign-in page
            b.States.Enqueue(FakeItvxBrowser.State("https://www.itv.com/watch/user/email-pair", signInPage: true)); // a code is emailed
            b.States.Enqueue(FakeItvxBrowser.State(signIn + "?step=password", signInPage: true, password: true));
            b.States.Enqueue(FakeItvxBrowser.State("https://www.itv.com/watch/user/signin?step=password", signInPage: true)); // submitting
            b.States.Enqueue(FakeItvxBrowser.State("https://www.itv.com/watch", signedIn: true));             // signed in
            PlaysAfterPress(b);

            await engine.PlayAsync(Episode);

            Assert.Equal(PlaybackState.Playing, engine.State);
            Assert.Equal(new[] { ("input[type=email]", "viewer@example.com"), ("input[type=password]", "s3cret-pass") }, b.Typed);
            Assert.Equal(new[] { "play", "button:continue", "use-password", "button:sign in", "play" }, b.Actions);
            Assert.Equal(new[] { Episode, signIn, Episode }, b.NavigatedUrls);
            Assert.Equal("ITVX", service.Hub.ActivePlayback);
        }

        [Fact]
        public async Task Use_password_instead_is_pressed_again_while_the_emailed_code_page_stays()
        {
            var (b, service, _) = Engine();
            var pair = FakeItvxBrowser.State("https://www.itv.com/watch/user/email-pair", signInPage: true);
            for (var i = 0; i < 30; i++) b.States.Enqueue(pair); // the first press came too early
            b.States.Enqueue(FakeItvxBrowser.State(ItvxService.SignInUrl + "?step=password", signInPage: true, password: true));
            b.LastState = FakeItvxBrowser.State("https://www.itv.com/watch", signedIn: true);

            Assert.Null(await service.SignInAsync(CancellationToken.None));

            var presses = b.Actions.Count(a => a == "use-password");
            Assert.InRange(presses, 2, 5);
            Assert.Contains(("input[type=password]", "s3cret-pass"), b.Typed);
        }

        [Fact]
        public async Task Without_credentials_a_sign_in_page_fails_saying_what_to_configure_and_frees_the_tab()
        {
            var (b, service, engine) = Engine(noCredentials: true);
            b.States.Enqueue(FakeItvxBrowser.State(Episode, playButton: true));
            b.LastState = FakeItvxBrowser.State(ItvxService.SignInUrl, signInPage: true, email: true);
            string? reason = null;
            engine.Failed += (_, r) => reason = r;

            await engine.PlayAsync(Episode);
            await engine.StopAsync();

            Assert.Equal(ItvxService.NoCredentialsMessage, reason);
            Assert.Empty(b.Typed);
            Assert.Equal(PlaybackState.Idle, engine.State);
            Assert.Null(service.Hub.ActivePlayback);
            Assert.True(b.HideWindowCalled);
            Assert.Equal(ItvxService.IdleUrl, b.NavigatedUrls.Last());
        }

        [Fact]
        public async Task A_refused_password_is_not_tried_again_in_the_same_run()
        {
            var (b, service, _) = Engine();
            b.States.Enqueue(FakeItvxBrowser.State(ItvxService.SignInUrl, signInPage: true, password: true));
            b.LastState = FakeItvxBrowser.State(ItvxService.SignInUrl, signInPage: true, text: "Incorrect email or password");

            var first = await service.SignInAsync(CancellationToken.None);
            var typed = b.Typed.Count;
            var second = await service.SignInAsync(CancellationToken.None);

            Assert.Contains("Incorrect email or password", first);
            Assert.Equal(first, second);
            Assert.Equal(typed, b.Typed.Count); // nothing typed the second time
        }

        [Fact]
        public async Task Nothing_playing_fails_with_the_last_page_and_frees_the_tab()
        {
            var (b, service, engine) = Engine();
            b.LastState = FakeItvxBrowser.State(Episode, signedIn: true, text: "Something went wrong");
            string? reason = null;
            engine.Failed += (_, r) => reason = r;

            await engine.PlayAsync(Episode);
            await engine.StopAsync();

            Assert.Contains("Something went wrong", reason);
            Assert.Null(service.Hub.ActivePlayback);
        }

        [Fact]
        public async Task Pause_play_and_seek_go_to_the_programme_video()
        {
            var (b, _, engine) = Engine();
            PlaysAfterPress(b);
            await engine.PlayAsync(Episode);
            await engine.PollOnceAsync();

            engine.Pause();
            Assert.Equal(PlaybackState.Paused, engine.State);
            engine.Resume();
            engine.SeekRelative(120);
            await engine.WhenCommandsDoneAsync();

            Assert.Equal(121, engine.TimePositionSeconds);
            Assert.Equal(3, b.ControlScripts.Count);
            Assert.Contains("'Pause'", b.ControlScripts[0]);
            Assert.Contains("'Play'", b.ControlScripts[1]);
            Assert.Contains("})(120)", b.ControlScripts[2]);
        }

        [Theory]
        [InlineData("https://www.itv.com/watch/vera/mz0gq64/1a7314a0002", false)] // ITVX moved on to the next episode
        [InlineData(Episode, true)]                                               // the video ended
        public async Task Leaving_the_title_or_its_end_ends_the_stream(string url, bool ended)
        {
            var (b, service, engine) = Engine();
            PlaysAfterPress(b);
            await engine.PlayAsync(Episode);
            var endedEvents = 0;
            engine.StreamEnded += (_, _) => endedEvents++;
            b.LastState = FakeItvxBrowser.State(url, signedIn: true, video: true, t: 10, duration: 5360, ended: ended);

            await engine.PollOnceAsync();

            Assert.Equal(1, endedEvents);
            Assert.Equal(PlaybackState.Idle, engine.State);
            await engine.StopAsync();
            Assert.Null(service.Hub.ActivePlayback);
        }

        [Fact]
        public void Only_watch_urls_are_played()
        {
            var (_, _, engine) = Engine();
            Assert.True(engine.CanPlay(Episode));
            Assert.False(engine.CanPlay("https://www.channel4.com/programmes/derry-girls"));
        }
    }

    public class ItvxServiceTests
    {
        [Fact]
        public async Task A_programme_page_is_fetched_from_an_itv_page_once_and_reused()
        {
            var (b, service, _) = ItvxPlaybackEngineTests.Engine();
            service.Hub.PageOwner = "Netflix";

            var p1 = await service.GetProgrammeAsync("vera", "mz0gq64", CancellationToken.None);
            var p2 = await service.GetProgrammeAsync("vera", "mz0gq64", CancellationToken.None);

            Assert.Same(p1, p2);
            Assert.Equal(new[] { ItvxService.IdleUrl }, b.NavigatedUrls); // onto itv.com first, since the tab was Netflix's
            Assert.Equal(new[] { "fetch:/watch/vera/mz0gq64" }, b.Actions);
            Assert.Equal("ITVX", service.Hub.PageOwner);
        }

        [Fact]
        public async Task A_programme_page_is_read_while_ITVX_plays_without_moving_the_tab()
        {
            var (b, service, _) = ItvxPlaybackEngineTests.Engine();
            service.Hub.PageOwner = "ITVX";
            service.IsPlaybackActive = true;

            await service.GetProgrammeAsync("vera", "mz0gq64", CancellationToken.None);

            Assert.Empty(b.NavigatedUrls);
        }

        [Fact]
        public async Task A_programme_page_is_refused_while_another_service_plays()
        {
            var (_, service, _) = ItvxPlaybackEngineTests.Engine();
            service.Hub.ActivePlayback = "Netflix";

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetProgrammeAsync("vera", "mz0gq64", CancellationToken.None));
            Assert.Contains("Netflix is playing", ex.Message);
        }

        [Fact]
        public async Task A_failed_page_fetch_is_loud()
        {
            var (b, service, _) = ItvxPlaybackEngineTests.Engine();
            b.FetchReply = new JsonObject { ["error"] = "HTTP 404" };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetProgrammeAsync("vera", "mz0gq64", CancellationToken.None));
            Assert.Contains("HTTP 404", ex.Message);
        }

        [Fact]
        public void Script_strings_are_escaped()
        {
            Assert.Equal("\"a\\u0022b\\u003C/script\\u003E\"", ItvxPlayerControl.JsString("a\"b</script>"));
        }
    }

    public class ItvxProviderChildrenTests
    {
        private static ItvxProvider Provider(FakeItvxBrowser? browser = null)
        {
            var b = browser ?? new FakeItvxBrowser { IsRunning = true };
            var service = new ItvxService(new BrowserHub(b), new FakeItvxHttp().Catalogue(), null);
            return new ItvxProvider(service.Catalogue, service);
        }

        [Fact]
        public async Task A_programme_with_several_series_gives_series_folders_in_ITVX_order()
        {
            var folders = (await Provider().GetChildrenAsync("itvx:vera/mz0gq64")).ToList();

            Assert.Equal(new[] { "Series 1", "Series 2" }, folders.Select(f => f.Name));
            Assert.Equal("itvx:vera/mz0gq64|1", folders[0].Id);
            Assert.Equal(MediaType.Brand, folders[0].Type);
            Assert.Equal("2 Episodes", folders[0].Details); // the Premium episode is not counted
            Assert.All(folders, f => Assert.Equal("ITVX", f.Source));
        }

        [Fact]
        public async Task A_series_folder_gives_its_episodes_ready_to_play()
        {
            var episodes = (await Provider().GetChildrenAsync("itvx:vera/mz0gq64|1")).ToList();

            Assert.Equal(new[] { "Hidden Depths", "Telling Tales" }, episodes.Select(e => e.Name));
            var e1 = episodes[0];
            Assert.Equal("itvx:ep:zz20f0t", e1.Id);
            Assert.DoesNotContain('|', e1.Id);
            Assert.Equal("S1:E1", e1.Details);
            Assert.Equal(MediaType.Video, e1.Type);
            Assert.Equal("https://www.itv.com/watch/vera/mz0gq64/1a7314a0001", e1.StreamUrl);
            Assert.Equal(TimeSpan.FromMinutes(89), e1.Duration);
        }

        [Fact]
        public async Task A_programme_with_one_series_lists_its_episodes_directly()
        {
            var b = new FakeItvxBrowser { IsRunning = true, FetchReply = JsonValue.Create(ItvxPageFixtures.Film) };
            var items = (await Provider(b).GetChildrenAsync("itvx:mr-christmas/bdgqfy6")).ToList();

            Assert.Single(items);
            Assert.Equal(MediaType.Video, items[0].Type);
        }

        [Theory]
        [InlineData("itvx:ep:zz20f0t")]
        [InlineData("channel4:derry-girls")]
        [InlineData("itvx:noslash")]
        public async Task Other_ids_have_no_children(string id)
        {
            var b = new FakeItvxBrowser { IsRunning = true };
            Assert.Empty(await Provider(b).GetChildrenAsync(id));
            Assert.Empty(b.Actions);
        }
    }
}
