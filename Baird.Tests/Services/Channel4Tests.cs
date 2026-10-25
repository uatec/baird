using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Baird.Mpv;
using Baird.Models;
using Baird.Services;
using Xunit;

namespace Baird.Tests.Services
{
    /// <summary>Shapes copied from channel4.com on 2026-10-25 (trimmed).</summary>
    internal static class Channel4Fixtures
    {
        public const string SearchReply = @"{""results"":[
            {""brand"":{""thumbnailUrl"":""https://ic.c4assets.com/brands/derry-girls/a35376d6.jpg?interpolation=progressive-bicubic&output-format=jpeg&output-quality=90&resize=120px:*"",""altText"":""Derry Girls"",""title"":""Derry Girls"",""highlight"":""*#*Derry Girls*%*"",""websafeTitle"":""derry-girls"",""description"":""Candid, family-centred comedy set during The Troubles in Northern Ireland"",""label"":"""",""source"":""search:results:::l1|::||"",""href"":""https://www.channel4.com/programmes/derry-girls"",""score"":""33.2""}},
            {""brand"":{""thumbnailUrl"":""https://ic.c4assets.com/vps/hot-fuzz-2007/D78ABC84.jpg?resize=120px:*"",""title"":""Hot Fuzz"",""websafeTitle"":""hot-fuzz"",""description"":""(2007) Comedy. A flashy cop is exiled to the countryside. But is it as sleepy as it seems?"",""label"":"""",""href"":""https://www.channel4.com/programmes/hot-fuzz""}},
            {""brand"":{""thumbnailUrl"":""https://ic.c4assets.com/x.jpg"",""title"":""Four Lions"",""websafeTitle"":""four-lions"",""description"":"""",""label"":""No episodes available to watch on demand"",""href"":""https://www.channel4.com/programmes/four-lions""}}
        ]}";

        /// <summary>A programme page: two series, an episode that is not out yet, and the page's bare <c>undefined</c> values.</summary>
        public static string SeriesPage => Page(@"{""initialData"":{""brand"":{""brandId"":""11154258"",""exclusive"":undefined,""websafeTitle"":""derry-girls"",""programmeType"":""MST"",""title"":""Derry Girls"",
            ""summary"":""It's {not} the \""end\"": undefined"",
            ""series"":[{""seriesNumber"":2,""title"":""Series 2""},{""seriesNumber"":1,""title"":""Series 1""}],
            ""episodes"":[
              {""seriesNumber"":2,""episodeNumber"":1,""programmeId"":""59741-007"",""title"":""Series 2 Episode 1"",""summary"":""Back to school."",""hrefLink"":""/programmes/derry-girls/on-demand/59741-007"",""durationLabel"":""24 mins"",""image"":{""src"":""https://ic.c4assets.com/brands/derry-girls/s2e1.jpg?imformat=chrome""},""nextOn"":undefined},
              {""seriesNumber"":1,""episodeNumber"":2,""programmeId"":""59741-002"",""title"":""Series 1 Episode 2"",""summary"":""The concert."",""hrefLink"":""/programmes/derry-girls/on-demand/59741-002"",""durationLabel"":""23 mins"",""image"":{""src"":""https://ic.c4assets.com/brands/derry-girls/s1e2.jpg""}},
              {""seriesNumber"":1,""episodeNumber"":1,""programmeId"":""59741-001"",""title"":""Series 1 Episode 1"",""summary"":""The first day."",""hrefLink"":""/programmes/derry-girls/on-demand/59741-001"",""durationLabel"":""23 mins"",""image"":{""src"":""https://ic.c4assets.com/brands/derry-girls/s1e1.jpg""}},
              {""seriesNumber"":3,""episodeNumber"":9,""programmeId"":""59741-099"",""title"":""Series 3 Episode 9"",""bottomText"":""Watch new episode from Fri 30 Oct"",""image"":{""src"":""x""}}
            ]},""isUserAuthenticated"":false}}");

        public static string FilmPage => Page(@"{""initialData"":{""brand"":{""websafeTitle"":""hot-fuzz"",""programmeType"":""FM"",""title"":""Hot Fuzz"",""series"":[],
            ""episodes"":[{""seriesNumber"":1,""episodeNumber"":1,""programmeId"":""77548-001"",""title"":"""",""hrefLink"":""/programmes/hot-fuzz/on-demand/77548-001"",""durationLabel"":""116 mins"",""image"":{""src"":""https://ic.c4assets.com/vps/hot-fuzz-2007/75DC.jpg?imformat=chrome""}}]}}}");

        public static string Page(string paramsLiteral) =>
            "<html><head><script>var other = {\"a\":1};</script></head><body><script>window.__PARAMS__ = " + paramsLiteral + ";\n window.x = 1;</script></body></html>";
    }

    public class Channel4CatalogueTests
    {
        [Fact]
        public void The_page_data_is_read_as_json_with_undefined_as_null_and_strings_left_alone()
        {
            var json = Channel4Catalogue.ExtractParamsJson(Channel4Fixtures.SeriesPage);
            Assert.NotNull(json);
            using var doc = JsonDocument.Parse(json!);
            var brand = doc.RootElement.GetProperty("initialData").GetProperty("brand");
            Assert.Equal(JsonValueKind.Null, brand.GetProperty("exclusive").ValueKind);
            Assert.Equal("It's {not} the \"end\": undefined", brand.GetProperty("summary").GetString());
            Assert.False(doc.RootElement.GetProperty("initialData").GetProperty("isUserAuthenticated").GetBoolean());
        }

        [Fact]
        public void A_page_without_programme_data_gives_a_reason()
        {
            Assert.Null(Channel4Catalogue.ExtractParamsJson("<html>Page not found</html>"));
            Assert.Null(Channel4Catalogue.ParseProgramme("<html>Page not found</html>", out var failure));
            Assert.Contains("__PARAMS__", failure);
        }

        [Fact]
        public void A_series_page_gives_its_watchable_episodes_in_series_and_episode_order()
        {
            var p = Channel4Catalogue.ParseProgramme(Channel4Fixtures.SeriesPage, out var failure);

            Assert.Null(failure);
            Assert.NotNull(p);
            Assert.False(p!.IsFilm);
            Assert.Equal("Derry Girls", p.Title);
            Assert.Equal(new[] { "59741-001", "59741-002", "59741-007" }, p.Episodes.Select(e => e.ProgrammeId)); // not the one not out yet
            Assert.Equal(new[] { 1, 2 }, p.SeriesNumbers);
            var e1 = p.Episodes[0];
            Assert.Equal(1380, e1.RuntimeSeconds);
            Assert.Equal("https://www.channel4.com/programmes/derry-girls/on-demand/59741-001", e1.PlayUrl);
            Assert.Equal("The first day.", e1.Description);
            Assert.StartsWith("https://ic.c4assets.com/brands/derry-girls/s1e1.jpg?", e1.ImageUrl);
            Assert.Contains("resize=480px", e1.ImageUrl);
        }

        [Fact]
        public void A_film_page_gives_one_item_named_after_the_film()
        {
            var p = Channel4Catalogue.ParseProgramme(Channel4Fixtures.FilmPage, out _);
            Assert.True(p!.IsFilm);
            var film = Assert.Single(p.Episodes);
            Assert.Equal("Hot Fuzz", film.Title); // the page leaves the film's episode title empty
            Assert.Equal(116 * 60, film.RuntimeSeconds);
        }

        [Fact]
        public void Search_lists_programmes_with_their_slug_bigger_images_and_whether_they_can_be_watched()
        {
            var results = Channel4Catalogue.ParseSearch(Channel4Fixtures.SearchReply)!;

            Assert.Equal(new[] { "derry-girls", "hot-fuzz", "four-lions" }, results.Select(r => r.Slug));
            Assert.Equal(new[] { true, true, false }, results.Select(r => r.Available));
            Assert.Equal(new[] { false, true, false }, results.Select(r => r.LooksLikeFilm));
            Assert.Equal("https://ic.c4assets.com/brands/derry-girls/a35376d6.jpg?interpolation=progressive-bicubic&output-format=jpeg&output-quality=90&resize=480px:*", results[0].ImageUrl);
        }

        [Theory]
        [InlineData("<html>")]
        [InlineData("{\"error\":\"x\"}")]
        [InlineData("")]
        public void A_reply_that_is_not_search_results_is_null(string body) => Assert.Null(Channel4Catalogue.ParseSearch(body));

        [Theory]
        [InlineData("23 mins", 1380)]
        [InlineData("116 mins", 6960)]
        [InlineData("1 hr 5 mins", 3900)]
        [InlineData("2 hours", 7200)]
        [InlineData("1 min", 60)]
        [InlineData("", 0)]
        [InlineData(null, 0)]
        public void Duration_labels(string? label, double seconds) => Assert.Equal(seconds, Channel4Catalogue.ParseDurationLabel(label));

        [Theory]
        [InlineData("https://www.channel4.com/programmes/derry-girls", true, "derry-girls", null)]
        [InlineData("https://www.channel4.com/programmes/derry-girls/on-demand/59741-001", true, "derry-girls", "59741-001")]
        [InlineData("https://www.channel4.com/programmes/hot-fuzz/on-demand/77548-001?x=1", true, "hot-fuzz", "77548-001")]
        [InlineData("https://www.channel4.com/robots.txt", false, "", null)]
        [InlineData("https://www.disneyplus.com/en-gb/play/abc", false, "", null)]
        [InlineData(null, false, "", null)]
        public void Page_urls(string? url, bool ok, string slug, string? pid)
        {
            Assert.Equal(ok, Channel4Catalogue.TryParsePageUrl(url, out var s, out var p));
            Assert.Equal(slug, s);
            Assert.Equal(pid, p);
            Assert.Equal(ok, Channel4PlaybackEngine.Handles(url));
        }
    }

    /// <summary>Answers Channel 4's search and programme pages from fixtures, and counts the requests.</summary>
    internal sealed class FakeChannel4Http : HttpMessageHandler
    {
        public Dictionary<string, string> Pages { get; } = new()
        {
            ["/programmes/derry-girls"] = Channel4Fixtures.SeriesPage,
            ["/programmes/hot-fuzz"] = Channel4Fixtures.FilmPage,
        };
        public List<string> Requests { get; } = new();
        public HttpStatusCode SearchStatus { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri.ToString());
            if (uri.Host == "all4nav.channel4.com")
                return Task.FromResult(new HttpResponseMessage(SearchStatus) { Content = new StringContent(Channel4Fixtures.SearchReply) });
            return Task.FromResult(Pages.TryGetValue(uri.AbsolutePath, out var html)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("<html>Page not found</html>") });
        }

        public Channel4Catalogue Catalogue() => new(new HttpClient(this));
    }

    public class Channel4ProviderTests
    {
        [Fact]
        public async Task Search_lists_watchable_programmes_a_film_plays_and_a_series_opens()
        {
            var provider = new Channel4Provider(new FakeChannel4Http().Catalogue());

            var items = (await provider.SearchAsync("derry")).ToList();

            Assert.Equal(new[] { "Derry Girls", "Hot Fuzz" }, items.Select(i => i.Name)); // not Four Lions: nothing to watch
            var series = items[0];
            Assert.Equal("channel4:derry-girls", series.Id);
            Assert.Equal(MediaType.Brand, series.Type);
            Assert.Equal("", series.StreamUrl);
            Assert.Equal("Channel 4", series.Source);
            var film = items[1];
            Assert.Equal(MediaType.Video, film.Type);
            Assert.Equal("https://www.channel4.com/programmes/hot-fuzz", film.StreamUrl);
            Assert.Equal("Film", film.Details);
        }

        [Fact]
        public async Task A_search_failure_throws_so_the_search_screen_shows_it()
        {
            var provider = new Channel4Provider(new FakeChannel4Http { SearchStatus = HttpStatusCode.ServiceUnavailable }.Catalogue());
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => provider.SearchAsync("derry"));
            Assert.Contains("503", ex.Message);
        }

        [Fact]
        public async Task A_series_with_several_series_opens_on_series_folders_newest_first_then_their_episodes()
        {
            var http = new FakeChannel4Http();
            var provider = new Channel4Provider(http.Catalogue());

            var folders = (await provider.GetChildrenAsync("channel4:derry-girls")).ToList();
            Assert.Equal(new[] { "channel4:derry-girls|2", "channel4:derry-girls|1" }, folders.Select(f => f.Id));
            Assert.Equal(new[] { "1 Episode", "2 Episodes" }, folders.Select(f => f.Details));
            Assert.All(folders, f => Assert.Equal(MediaType.Brand, f.Type));

            var episodes = (await provider.GetChildrenAsync("channel4:derry-girls|1")).ToList();
            Assert.Equal(new[] { "channel4:ep:59741-001", "channel4:ep:59741-002" }, episodes.Select(e => e.Id));
            Assert.Equal("S1:E1", episodes[0].Details);
            Assert.Equal(TimeSpan.FromMinutes(23), episodes[0].Duration);
            Assert.Equal("https://www.channel4.com/programmes/derry-girls/on-demand/59741-001", episodes[0].StreamUrl);
            Assert.All(episodes, e => Assert.DoesNotContain("|", e.Id));

            Assert.Single(http.Requests); // the page is read once and reused
        }

        [Fact]
        public async Task A_film_page_opens_on_the_film_and_other_providers_ids_are_ignored()
        {
            var provider = new Channel4Provider(new FakeChannel4Http().Catalogue());

            var film = Assert.Single(await provider.GetChildrenAsync("channel4:hot-fuzz"));
            Assert.Equal("Film", film.Details);
            Assert.Equal("Hot Fuzz", film.Name);

            Assert.Empty(await provider.GetChildrenAsync("disney:abc"));
            Assert.Empty(await provider.GetChildrenAsync("channel4:ep:77548-001"));
        }
    }

    /// <summary>A shared browser showing Channel 4: page states in order, then the last one for ever.</summary>
    internal sealed class FakeChannel4Browser : Baird.Tests.ViewModels.MockCdpBrowserService, ICdpPageDriver
    {
        public Queue<JsonObject> States { get; } = new();
        public JsonObject LastState { get; set; } = State("https://www.channel4.com/robots.txt");
        public List<string> Typed { get; } = new();
        public int Enters { get; private set; }
        public List<string> ControlScripts { get; } = new();
        public bool StyleApplied { get; private set; }

        public static JsonObject State(string url, bool? auth = null, bool signInPrompt = false, bool email = false, bool password = false,
            bool video = false, double t = 0, double duration = 0, bool paused = false, bool ended = false, bool otherVideo = false, string text = "",
            bool cancelled = false) => new()
            {
                ["cancelled"] = cancelled,
                ["url"] = url,
                ["authenticated"] = auth,
                ["signInPrompt"] = signInPrompt,
                ["emailField"] = email,
                ["passwordField"] = password,
                ["hasVideo"] = video,
                ["currentTime"] = t,
                ["duration"] = duration,
                ["paused"] = paused,
                ["ended"] = ended,
                ["otherVideoPlaying"] = otherVideo,
                ["text"] = text,
            };

        public Task<JsonElement?> EvaluateAsync(string script, CancellationToken ct = default)
        {
            string json;
            if (script.Contains("signInPrompt:"))
            {
                json = (States.Count > 0 ? States.Dequeue() : LastState).ToJsonString();
            }
            else if (script.Contains("__bairdChannel4Style")) { StyleApplied = true; json = "true"; }
            else { ControlScripts.Add(script); json = "\"video\""; }
            return Task.FromResult<JsonElement?>(JsonDocument.Parse(json).RootElement.Clone());
        }

        public Task<bool> TypeIntoAsync(string selector, string text, CancellationToken ct = default) { Typed.Add(selector); return Task.FromResult(true); }
        public Task PressEnterAsync(CancellationToken ct = default) { Enters++; return Task.CompletedTask; }
        public Task MoveMouseAsync(int x, int y, CancellationToken ct = default) => Task.CompletedTask;
        public Task<CapturedRequest?> NavigateAndCaptureAsync(string url, Func<CapturedRequest, bool> match, TimeSpan timeout, CancellationToken ct = default) =>
            Task.FromResult<CapturedRequest?>(null);
    }

    public class Channel4PlaybackEngineTests
    {
        internal const string Episode = "https://www.channel4.com/programmes/derry-girls/on-demand/59741-001";

        internal static (FakeChannel4Browser Browser, Channel4Service Service, Channel4PlaybackEngine Engine) Engine()
        {
            var b = new FakeChannel4Browser { IsRunning = true };
            var service = new Channel4Service(new BrowserHub(b), new FakeChannel4Http().Catalogue())
            {
                PollInterval = TimeSpan.FromMilliseconds(2),
            };
            var engine = new Channel4PlaybackEngine(service)
            {
                PollInterval = TimeSpan.FromHours(1), // tests poll by hand
                StartTimeout = TimeSpan.FromSeconds(2),
            };
            return (b, service, engine);
        }

        internal static void Plays(FakeChannel4Browser b, string url = Episode, double duration = 1395)
        {
            b.States.Enqueue(FakeChannel4Browser.State(url, auth: true, video: true, t: 0, duration: duration));
            b.States.Enqueue(FakeChannel4Browser.State(url, auth: true, video: true, t: 0.5, duration: duration));
            b.LastState = FakeChannel4Browser.State(url, auth: true, video: true, t: 1.0, duration: duration);
        }

        [Fact]
        public async Task A_signed_in_episode_plays_with_the_page_hidden_and_the_tab_marked_as_playing()
        {
            var (b, service, engine) = Engine();
            Plays(b);
            var loaded = 0;
            engine.FileLoaded += (_, _) => loaded++;

            await engine.PlayAsync(Episode);

            Assert.Equal(PlaybackState.Playing, engine.State);
            Assert.Equal(1, loaded);
            Assert.Contains(Episode, b.NavigatedUrls);
            Assert.True(b.StyleApplied);
            Assert.Equal("Channel 4", service.Hub.ActivePlayback);
            Assert.Equal("Channel 4", service.Hub.PageOwner);
            Assert.Empty(b.Typed); // no sign-in needed

            await engine.PollOnceAsync();
            Assert.Equal(1395, engine.DurationSeconds); // the page's own length
            Assert.Equal(1.0, engine.TimePositionSeconds);
        }

        [Theory]
        [InlineData(false, true)]  // the on-demand page: isUserAuthenticated false, "Please sign in to watch"
        [InlineData(null, false)]  // redirected to the sign-in form
        public async Task A_page_that_asks_for_a_sign_in_fails_offering_the_sign_in_types_nothing_and_frees_the_tab(bool? auth, bool prompt)
        {
            var (b, service, engine) = Engine();
            b.LastState = auth == null
                ? FakeChannel4Browser.State(Channel4Service.SignInUrl, email: true)
                : FakeChannel4Browser.State(Episode, auth: auth, signInPrompt: prompt, text: "Please sign in to watch");
            string? reason = null;
            engine.Failed += (_, r) => reason = r;

            await engine.PlayAsync(Episode);
            await engine.StopAsync();

            Assert.Equal(Channel4Service.NotSignedInMessage, reason);
            Assert.Equal(Channel4PlaybackEngine.SignInAction, engine.FailureAction);
            Assert.Empty(b.Typed);
            Assert.Equal(0, b.Enters);
            Assert.Equal(PlaybackState.Idle, engine.State);
            Assert.Null(service.Hub.ActivePlayback);
            Assert.Equal(Channel4Service.IdleUrl, b.NavigatedUrls.Last());
        }

        private const string SignInPage = "https://www.channel4.com/sign-in-or-register?redirectUrl=x";
        private const string ProgrammePage = "https://www.channel4.com/programmes/derry-girls";

        private static async Task<Channel4PlaybackEngine> NotSignedIn(FakeChannel4Browser b, Channel4PlaybackEngine engine)
        {
            b.States.Enqueue(FakeChannel4Browser.State(Episode, auth: false, signInPrompt: true));
            await engine.PlayAsync(Episode);
            Assert.Equal(Channel4PlaybackEngine.SignInAction, engine.FailureAction);
            return engine;
        }

        [Fact]
        public async Task OK_after_not_signed_in_shows_the_sign_in_page_above_Baird_waits_for_the_viewer_then_gives_the_tab_back()
        {
            var (b, service, engine) = Engine();
            await NotSignedIn(b, engine);
            b.States.Enqueue(FakeChannel4Browser.State(SignInPage, email: true));
            b.States.Enqueue(FakeChannel4Browser.State(SignInPage, password: true));
            b.LastState = FakeChannel4Browser.State(ProgrammePage, auth: true); // Channel 4 sent the browser back, signed in

            var outcome = await engine.RunFailureActionAsync();

            Assert.Null(outcome);
            Assert.True(b.RevealWindowCalled);
            Assert.True(b.HideWindowCalled);
            Assert.Contains(b.NavigatedUrls, u => u == Channel4Service.SignInUrl + "?redirectUrl=" + Uri.EscapeDataString(ProgrammePage));
            Assert.Equal(Channel4Service.IdleUrl, b.NavigatedUrls.Last());
            Assert.Null(service.Hub.ActivePlayback);
            Assert.Empty(b.Typed); // the viewer types, not Baird
            Assert.Equal(0, b.Enters);
            Assert.Null(engine.FailureAction);
        }

        [Fact]
        public async Task Back_on_the_sign_in_page_cancels_and_OK_offers_it_again()
        {
            var (b, service, engine) = Engine();
            await NotSignedIn(b, engine);
            b.States.Enqueue(FakeChannel4Browser.State(SignInPage, email: true));
            b.LastState = FakeChannel4Browser.State(SignInPage, email: true, cancelled: true);

            var outcome = await engine.RunFailureActionAsync();

            Assert.Equal("Channel 4 sign-in cancelled.", outcome);
            Assert.Equal(Channel4PlaybackEngine.SignInAction, engine.FailureAction);
            Assert.True(b.HideWindowCalled);
            Assert.Null(service.Hub.ActivePlayback);
        }

        [Fact]
        public async Task Closing_the_sign_in_page_counts_as_cancelling()
        {
            var (b, service, engine) = Engine();
            await NotSignedIn(b, engine);
            b.States.Enqueue(FakeChannel4Browser.State(SignInPage, email: true));
            b.LastState = FakeChannel4Browser.State(ProgrammePage, auth: false); // the page's X: back to the programme, signed out

            Assert.Equal("Channel 4 sign-in cancelled.", await engine.RunFailureActionAsync());
            Assert.Null(service.Hub.ActivePlayback);
        }

        [Fact]
        public async Task Nobody_signing_in_gives_up_after_the_timeout()
        {
            var (b, service, engine) = Engine();
            service.HandSignInTimeout = TimeSpan.FromMilliseconds(100);
            await NotSignedIn(b, engine);
            b.LastState = FakeChannel4Browser.State(SignInPage, email: true);

            var outcome = await engine.RunFailureActionAsync();

            Assert.Contains("too long", outcome);
            Assert.Null(service.Hub.ActivePlayback);
        }

        [Fact]
        public async Task Other_failures_offer_nothing()
        {
            var (b, _, engine) = Engine();
            b.LastState = FakeChannel4Browser.State(Episode, auth: true, video: true, t: 0);

            await engine.PlayAsync(Episode);

            Assert.Null(engine.FailureAction);
            Assert.Equal("There is nothing to do.", await engine.RunFailureActionAsync());
        }

        [Fact]
        public async Task A_video_that_never_plays_fails_with_the_page_text()
        {
            var (b, service, engine) = Engine();
            b.LastState = FakeChannel4Browser.State(Episode, auth: true, video: true, t: 0, text: "Something went wrong");
            string? reason = null;
            engine.Failed += (_, r) => reason = r;

            await engine.PlayAsync(Episode);

            Assert.Equal(PlaybackState.Idle, engine.State);
            Assert.Contains("Something went wrong", reason);
            Assert.Null(service.Hub.ActivePlayback);
        }

        [Fact]
        public async Task A_film_from_search_is_played_by_its_on_demand_page_with_its_length()
        {
            var (b, _, engine) = Engine();
            const string film = "https://www.channel4.com/programmes/hot-fuzz/on-demand/77548-001";
            Plays(b, film, duration: 0);
            double? duration = null;
            engine.DurationChanged += (_, d) => duration = d;

            await engine.PlayAsync("https://www.channel4.com/programmes/hot-fuzz");

            Assert.Equal(PlaybackState.Playing, engine.State);
            Assert.Contains(film, b.NavigatedUrls);
            Assert.Equal(116 * 60, duration); // from the programme page, while the video does not know it
        }

        [Fact]
        public async Task An_advert_does_not_move_the_position_or_take_the_length_and_cannot_be_skipped()
        {
            var (b, _, engine) = Engine();
            Plays(b);
            await engine.PlayAsync(Episode);
            await engine.PollOnceAsync(); // programme at 1 s
            b.LastState = FakeChannel4Browser.State(Episode, auth: true, video: true, t: 12, duration: 30); // an advert in the same element

            await engine.PollOnceAsync();
            engine.SeekRelative(60);
            await engine.WhenCommandsDoneAsync();

            Assert.True(engine.IsAdvertPlaying);
            Assert.Equal(1.0, engine.TimePositionSeconds);
            Assert.Equal(1395, engine.DurationSeconds);
            Assert.Empty(b.ControlScripts);

            b.LastState = FakeChannel4Browser.State(Episode, auth: true, video: true, t: 2, duration: 1395);
            await engine.PollOnceAsync();
            Assert.False(engine.IsAdvertPlaying);
            Assert.Equal(2, engine.TimePositionSeconds);
        }

        [Fact]
        public async Task Pause_play_and_seek_go_to_the_page_video()
        {
            var (b, _, engine) = Engine();
            Plays(b);
            await engine.PlayAsync(Episode);
            await engine.PollOnceAsync();

            engine.Pause();
            Assert.Equal(PlaybackState.Paused, engine.State);
            engine.Resume();
            engine.SeekRelative(30);
            await engine.WhenCommandsDoneAsync();

            Assert.Equal(3, b.ControlScripts.Count);
            Assert.Contains("('Pause')", b.ControlScripts[0]);
            Assert.Contains("('Play')", b.ControlScripts[1]);
            Assert.Contains("(30)", b.ControlScripts[2]);
            Assert.Equal(31, engine.TimePositionSeconds);
        }

        [Fact]
        public async Task The_page_moving_to_another_programme_ends_the_title_and_frees_the_tab()
        {
            var (b, service, engine) = Engine();
            Plays(b);
            await engine.PlayAsync(Episode);
            var ended = 0;
            engine.StreamEnded += (_, _) => ended++;
            b.LastState = FakeChannel4Browser.State("https://www.channel4.com/programmes/derry-girls/on-demand/59741-002", auth: true, video: true, t: 3);

            await engine.PollOnceAsync();
            await engine.StopAsync();

            Assert.Equal(1, ended);
            Assert.Equal(PlaybackState.Idle, engine.State);
            Assert.Null(service.Hub.ActivePlayback);
            Assert.Equal(Channel4Service.IdleUrl, b.NavigatedUrls.Last());
        }

        [Fact]
        public async Task Stopping_while_another_service_still_plays_leaves_the_tab_alone()
        {
            var (b, service, engine) = Engine();
            service.Hub.ActivePlayback = "Netflix";
            string? reason = null;
            engine.Failed += (_, r) => reason = r;

            var play = engine.PlayAsync(Episode);
            // The wait is 15 s; ending it early is not needed: stopping Baird's player cancels it.
            await Task.Delay(50);
            await engine.StopAsync();
            await play;

            Assert.Null(reason);
            Assert.Empty(b.NavigatedUrls);
            Assert.Equal("Netflix", service.Hub.ActivePlayback);
        }
    }
}
