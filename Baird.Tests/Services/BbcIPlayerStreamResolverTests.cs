using Baird.Services;
using Xunit;

namespace Baird.Tests.Services
{
    public class BbcIPlayerStreamResolverTests
    {
        [Theory]
        [InlineData("https://www.bbc.co.uk/iplayer/episode/m001ttsn", "m001ttsn")]
        [InlineData("https://www.bbc.co.uk/iplayer/episode/m001ttsn/the-traitors-series-2-episode-1", "m001ttsn")]
        public void TryGetEpisodePid_ExtractsPidFromEpisodeUrls(string url, string expected)
        {
            Assert.True(BbcIPlayerStreamResolver.TryGetEpisodePid(url, out var pid));
            Assert.Equal(expected, pid);
        }

        [Theory]
        [InlineData("https://www.youtube.com/watch?v=TI5wzoCI474")]
        [InlineData("https://vod-hls-uk-live.akamaized.net/usp/auth/vod/x.m3u8")]
        [InlineData("")]
        public void TryGetEpisodePid_IgnoresOtherUrls(string url)
        {
            Assert.False(BbcIPlayerStreamResolver.TryGetEpisodePid(url, out _));
        }

        [Fact]
        public void ParseVersionPid_PrefersOriginalVersion()
        {
            const string json = """
                {"episodes":[{"id":"m001ttsn","versions":[
                  {"id":"p0h2rfbf","kind":"audio-described"},
                  {"id":"m001ttsp","kind":"original"},
                  {"id":"m001v687","kind":"signed"}]}]}
                """;
            Assert.Equal("m001ttsp", BbcIPlayerStreamResolver.ParseVersionPid(json));
        }

        [Fact]
        public void ParseVersionPid_FallsBackToFirstVersion_AndNullWhenNone()
        {
            Assert.Equal("a1", BbcIPlayerStreamResolver.ParseVersionPid("""{"episodes":[{"versions":[{"id":"a1","kind":"signed"}]}]}"""));
            Assert.Null(BbcIPlayerStreamResolver.ParseVersionPid("""{"episodes":[]}"""));
        }

        [Fact]
        public void ParseBestHlsUrl_PicksHighestBitrateVideo_LowestPriorityHttpsHls()
        {
            const string json = """
                {"media":[
                  {"kind":"thumbnails","connection":[{"protocol":"https","transferFormat":"plain","priority":"10","href":"https://thumb"}]},
                  {"kind":"video","bitrate":"1800","connection":[{"protocol":"https","transferFormat":"hls","priority":"10","href":"https://low"}]},
                  {"kind":"video","bitrate":"8490","connection":[
                    {"protocol":"http","transferFormat":"hls","priority":"10","href":"http://akamai"},
                    {"protocol":"https","transferFormat":"hls","priority":"30","href":"https://cloudfront"},
                    {"protocol":"https","transferFormat":"hls","priority":"10","href":"https://akamai"},
                    {"protocol":"https","transferFormat":"dash","priority":"5","href":"https://dash"}]},
                  {"kind":"captions","connection":[]}]}
                """;
            Assert.Equal("https://akamai", BbcIPlayerStreamResolver.ParseBestHlsUrl(json));
        }

        [Fact]
        public void ParseBestHlsUrl_ReturnsNullForMediaselectorError()
        {
            Assert.Null(BbcIPlayerStreamResolver.ParseBestHlsUrl("""{"result":"selectionunavailable"}"""));
        }
    }

    /// <summary>iPlayer is asked about every id Baird knows; only BBC programme ids are its business.</summary>
    public class BbcIPlayerIdTests
    {
        [Theory]
        [InlineData("b006q2x0", true)]
        [InlineData("m001z6w8", true)]
        [InlineData("p0f1x2yz", true)]
        [InlineData("netflix:81009946", false)]
        [InlineData("disney:ep:7a1389f2-c391-4b2c-aa3c-2e862cc79c68", false)]
        [InlineData("itvx:the-bay/10_0001", false)]
        [InlineData("yt:channel/UCSZ5XEGmglFy6DVVJM-5SFQ", false)]
        [InlineData("f86b74a893a73c4671146bf33076c286", false)]   // a Jellyfin or TvHeadend id
        [InlineData("qN9GRs0XOu0", false)]                          // a YouTube video
        [InlineData("", false)]
        public void Only_a_bbc_programme_id_is_one_of_iplayers(string id, bool expected) =>
            Assert.Equal(expected, BbcIPlayerService.IsPid(id));
    }
}
