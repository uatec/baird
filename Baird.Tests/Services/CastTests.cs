using System.Text;
using System.Text.Json;
using Baird.Services.Cast;

namespace Baird.Tests.Services
{
    public class CastLinksTests
    {
        [Theory]
        [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc123", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
        [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&pp=xyz", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
        [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
        [InlineData("https://youtube.com/shorts/abcDEF12345?feature=share", "https://www.youtube.com/watch?v=abcDEF12345")]
        [InlineData("https://www.youtube.com/live/abcDEF12345", "https://www.youtube.com/watch?v=abcDEF12345")]
        [InlineData("https://www.youtube.com/playlist?list=PL123", "https://www.youtube.com/playlist?list=PL123")]
        public void YouTube_links_become_watch_pages(string shared, string expected)
        {
            var link = CastLinks.Resolve(shared)!;
            Assert.Equal(expected, link.PlayUrl);
            Assert.Equal("YouTube", link.Service);
        }

        [Theory]
        [InlineData("https://youtu.be/dQw4w9WgXcQ?t=90", 90)]
        [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=1m30s", 90)]
        [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=1h0m5s", 3605)]
        [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", 0)]
        public void YouTube_start_times_are_kept(string shared, int seconds) =>
            Assert.Equal(TimeSpan.FromSeconds(seconds), CastLinks.Resolve(shared)!.Start);

        [Theory]
        [InlineData("https://www.netflix.com/title/80057281?s=i&trkid=1&vlang=en", "https://www.netflix.com/watch/80057281")]
        [InlineData("https://www.netflix.com/gb/title/80057281", "https://www.netflix.com/watch/80057281")]
        [InlineData("https://www.netflix.com/watch/80057281?trackId=1", "https://www.netflix.com/watch/80057281")]
        [InlineData("https://www.netflix.com/browse?jbv=80057281", "https://www.netflix.com/watch/80057281")]
        public void Netflix_title_pages_become_watch_pages(string shared, string expected)
        {
            var link = CastLinks.Resolve(shared)!;
            Assert.Equal(expected, link.PlayUrl);
            Assert.Equal("Netflix", link.Service);
        }

        [Fact]
        public void IPlayer_episode_links_lose_their_slug_and_tracking()
        {
            var link = CastLinks.Resolve("https://www.bbc.co.uk/iplayer/episode/m001abcd/some-show-series-1-1-pilot?xtor=AL-1")!;
            Assert.Equal("https://www.bbc.co.uk/iplayer/episode/m001abcd", link.PlayUrl);
            Assert.Equal("BBC iPlayer", link.Service);
        }

        [Fact]
        public void Browser_services_keep_their_pages_without_tracking()
        {
            Assert.Equal(("https://www.itv.com/watch/love-island/2a7042", "ITVX"),
                CastLinks.Resolve("https://www.itv.com/watch/love-island/2a7042?utm_source=share") is { } i ? (i.PlayUrl, i.Service) : default);
            Assert.Equal("Channel 4", CastLinks.Resolve("https://www.channel4.com/programmes/the-great-british-bake-off/on-demand/12345-001")!.Service);
            Assert.Equal("Disney+", CastLinks.Resolve("https://disneyplus.com/browse/entity-123")!.Service);
            Assert.Equal("https://www.disneyplus.com/browse/entity-123", CastLinks.Resolve("https://disneyplus.com/browse/entity-123")!.PlayUrl);
        }

        [Fact]
        public void Anything_else_on_the_web_goes_to_mpv_named_by_its_site()
        {
            var link = CastLinks.Resolve("https://vimeo.com/76979871")!;
            Assert.Equal("https://vimeo.com/76979871", link.PlayUrl);
            Assert.Equal("vimeo.com", link.Service);
        }

        [Fact]
        public void A_link_is_found_in_shared_text()
        {
            var link = CastLinks.Resolve("Check this out! https://youtu.be/dQw4w9WgXcQ.")!;
            Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", link.PlayUrl);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("just some words")]
        [InlineData("ftp://example.com/video.mp4")]
        [InlineData("https://youtu.be/")]
        public void Not_a_playable_link(string? shared) => Assert.Null(CastLinks.Resolve(shared));
    }

    public class PhonePairingsTests : IDisposable
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("baird-phones-").FullName;
        private DateTimeOffset _now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        private string PathOf => Path.Combine(_dir, "phones.json");
        public void Dispose() => Directory.Delete(_dir, true);

        [Fact]
        public void An_issued_token_is_accepted_and_counts_once_used()
        {
            var p = new PhonePairings(PathOf, () => _now);
            var token = p.Issue();
            Assert.Equal(0, p.Connected);
            Assert.True(p.Accept(token));
            Assert.Equal(1, p.Connected);
            Assert.False(p.Accept("nope"));
            Assert.False(p.Accept(null));
        }

        [Fact]
        public void Tokens_survive_a_restart()
        {
            var token = new PhonePairings(PathOf, () => _now).Issue();
            Assert.True(new PhonePairings(PathOf, () => _now).Accept(token));
        }

        [Fact]
        public void Unused_tokens_expire_after_a_day_but_used_ones_stay()
        {
            var p = new PhonePairings(PathOf, () => _now);
            var used = p.Issue();
            var unused = p.Issue();
            Assert.True(p.Accept(used));
            _now += TimeSpan.FromDays(2);
            Assert.False(p.Accept(unused));
            Assert.True(p.Accept(used));
        }

        [Fact]
        public void Forgetting_phones_revokes_every_token()
        {
            var p = new PhonePairings(PathOf, () => _now);
            var token = p.Issue();
            p.Accept(token);
            p.ForgetAll();
            Assert.False(p.Accept(token));
            Assert.Equal(0, p.Connected);
        }
    }

    public class CastApiTests
    {
        private readonly PhonePairings _pairings = new(null, () => DateTimeOffset.Now);
        private readonly List<CastLink> _played = new();
        private string? _shortcut;

        private CastApi Api() => new(_pairings, link => { _played.Add(link); return Task.FromResult("Playing it"); }, () => _shortcut);

        private static (string Status, string Message) Read(CastResponse r)
        {
            Assert.Equal(200, r.Status);
            using var doc = JsonDocument.Parse(r.Body);
            return (doc.RootElement.GetProperty("status").GetString()!, doc.RootElement.GetProperty("message").GetString()!);
        }

        [Fact]
        public async Task A_paired_phone_plays_a_link()
        {
            var token = _pairings.Issue();
            var r = Read(await Api().HandleAsync(new CastRequest("POST", $"/phone/{token}", """{"url":"https://youtu.be/dQw4w9WgXcQ"}""")));
            Assert.Equal(("ok", "Playing it"), r);
            Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", Assert.Single(_played).PlayUrl);
        }

        [Fact]
        public async Task Plain_text_bodies_work_too()
        {
            var token = _pairings.Issue();
            await Api().HandleAsync(new CastRequest("POST", $"/phone/{token}", "https://vimeo.com/76979871"));
            Assert.Single(_played);
        }

        [Fact]
        public async Task An_unknown_phone_is_told_to_pair_again_and_nothing_plays()
        {
            _pairings.Issue();
            var r = Read(await Api().HandleAsync(new CastRequest("POST", "/phone/wrong", """{"url":"https://youtu.be/dQw4w9WgXcQ"}""")));
            Assert.Equal("unpaired", r.Status);
            Assert.Empty(_played);
            Assert.Equal("unpaired", Read(await Api().HandleAsync(new CastRequest("POST", "/phone/", "https://youtu.be/dQw4w9WgXcQ"))).Status);
        }

        [Theory]
        [InlineData("""{"url":""}""")]
        [InlineData("""{"url":"not a link"}""")]
        [InlineData("")]
        public async Task Nothing_playable_is_an_error_message(string body)
        {
            var token = _pairings.Issue();
            var r = Read(await Api().HandleAsync(new CastRequest("POST", $"/phone/{token}", body)));
            Assert.Equal("error", r.Status);
            Assert.Empty(_played);
        }

        [Fact]
        public async Task A_failed_play_is_reported_not_thrown()
        {
            var token = _pairings.Issue();
            var api = new CastApi(_pairings, _ => throw new InvalidOperationException("boom"), () => null);
            Assert.Equal("error", Read(await api.HandleAsync(new CastRequest("POST", $"/phone/{token}", "https://youtu.be/dQw4w9WgXcQ"))).Status);
        }

        [Fact]
        public async Task The_setup_page_links_the_shortcut_when_there_is_one()
        {
            var without = await Api().HandleAsync(new CastRequest("GET", "/", ""));
            Assert.DoesNotContain("href=\"/shortcut\"", Encoding.UTF8.GetString(without.Body));

            _shortcut = Path.GetTempFileName();
            File.WriteAllBytes(_shortcut, new byte[] { 1, 2, 3 });
            try
            {
                var page = await Api().HandleAsync(new CastRequest("GET", "/", ""));
                Assert.Contains("href=\"/shortcut\"", Encoding.UTF8.GetString(page.Body));
                var file = await Api().HandleAsync(new CastRequest("GET", "/shortcut", ""));
                Assert.Equal(new byte[] { 1, 2, 3 }, file.Body);
                Assert.Equal("Play on Baird.shortcut", file.FileName);
            }
            finally { File.Delete(_shortcut); }
        }

        [Fact]
        public async Task The_camera_opening_a_phone_address_gets_the_setup_page_and_nothing_plays()
        {
            var token = _pairings.Issue();
            var page = await Api().HandleAsync(new CastRequest("GET", $"/phone/{token}", ""));
            Assert.Equal(200, page.Status);
            Assert.StartsWith("text/html", page.ContentType);
            Assert.Empty(_played);
            Assert.Equal("http://192.168.1.20:8099/phone/abc", CastApi.PhoneUrl("http://192.168.1.20:8099/", "abc"));
        }

        [Fact]
        public async Task The_shortcut_is_missing_until_a_build_ships_it() =>
            Assert.Equal(404, (await Api().HandleAsync(new CastRequest("GET", "/shortcut", ""))).Status);

        [Theory]
        [InlineData("""{"url":["https://youtu.be/dQw4w9WgXcQ"]}""", "https://youtu.be/dQw4w9WgXcQ")]
        [InlineData("""{"url":"x"}""", "x")]
        [InlineData("""{"other":"x"}""", "")]
        [InlineData("{not json", "{not json")]
        public void Shared_text_is_read_from_the_body(string body, string expected) => Assert.Equal(expected, CastApi.SharedText(body));

        [Fact]
        public void YouTube_titles_come_from_oembed()
        {
            Assert.Equal("Never Gonna Give You Up", CastTitles.ParseOEmbedTitle("""{"title":"Never Gonna Give You Up","author_name":"Rick"}"""));
            Assert.Null(CastTitles.ParseOEmbedTitle("<html>"));
            Assert.Equal("Video from vimeo.com", CastTitles.Fallback(new CastLink("https://vimeo.com/1", "vimeo.com")));
        }

        [Theory]
        [InlineData("192.168.1.20", true)]
        [InlineData("10.0.0.5", true)]
        [InlineData("172.20.1.1", true)]
        [InlineData("100.101.102.103", false)] // Tailscale: guests can't reach it
        [InlineData("8.8.8.8", false)]
        public void Only_home_network_addresses_go_in_the_qr(string ip, bool home) =>
            Assert.Equal(home, CastServer.IsHomeNetwork(System.Net.IPAddress.Parse(ip)));
    }

    public class CastServerTests
    {
        [Fact]
        public async Task The_server_answers_over_http()
        {
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var pairings = new PhonePairings(null, () => DateTimeOffset.Now);
            var token = pairings.Issue();
            string? played = null;
            using var server = new CastServer(new CastApi(pairings, l => { played = l.PlayUrl; return Task.FromResult("ok"); }, () => null), port);
            server.Start();

            using var http = new HttpClient();
            var page = await http.GetAsync($"http://127.0.0.1:{port}/");
            Assert.Equal(System.Net.HttpStatusCode.OK, page.StatusCode);

            var req = new HttpRequestMessage(HttpMethod.Post, CastApi.PhoneUrl($"http://127.0.0.1:{port}", token))
            {
                Content = new StringContent("""{"url":"https://youtu.be/dQw4w9WgXcQ"}""", Encoding.UTF8, "application/json"),
            };
            var res = await http.SendAsync(req);
            Assert.Contains("\"status\":\"ok\"", await res.Content.ReadAsStringAsync());
            Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", played);
        }
    }
}
