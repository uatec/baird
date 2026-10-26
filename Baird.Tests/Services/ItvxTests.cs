using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Baird.Models;
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

    public class ItvxCatalogueTests
    {
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
}
