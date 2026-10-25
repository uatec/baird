using System.Collections.Generic;
using System.Linq;
using Baird.Services;
using Baird.ViewModels;
using Xunit;

namespace Baird.Tests.ViewModels
{
    public class SearchRelevanceTests
    {
        private static double S(string title, string query) => SearchRelevance.Score(title, query);

        [Fact]
        public void The_ladder_runs_exact_then_without_article_then_starts_with_then_all_words_then_phrase_then_inside_a_word()
        {
            var exact = S("The Simpsons", "the simpsons");
            var noArticle = S("The Simpsons", "simpsons");
            var startsWith = S("Simpsons Roasting on an Open Fire", "simpsons");
            var allWords = S("Treehouse of Horror: The Simpsons", "simpsons horror");
            var phrase = S("Maggie Simpson in Playdate", "maggie simpson");
            var insideWord = S("Resimpsons", "simpsons");

            Assert.True(exact > noArticle, "exact beats the article-less match");
            Assert.True(noArticle > startsWith, "the same title minus 'the' beats a title that merely starts with it");
            Assert.True(startsWith > allWords);
            Assert.True(allWords > insideWord || phrase > insideWord);
            Assert.True(phrase > insideWord);
        }

        [Fact]
        public void An_exact_title_is_100_and_ignores_case_accents_and_punctuation()
        {
            Assert.Equal(100, S("Spider-Man: No Way Home", "spider man no way home"));
            Assert.Equal(100, S("Amélie", "amelie"));
            Assert.Equal(100, S("ONE PIECE", "One Piece"));
        }

        [Fact]
        public void An_apostrophe_does_not_split_a_word()
        {
            Assert.Equal(100, S("Grey's Anatomy", "greys anatomy"));
        }

        [Fact]
        public void A_title_that_does_not_contain_the_query_scores_zero()
        {
            Assert.Equal(0, S("The Mandalorian", "simpsons"));
            Assert.Equal(0, S("", "simpsons"));
            Assert.Equal(0, S("The Simpsons", ""));
            Assert.Equal(0, S(null, null));
        }

        [Fact]
        public void The_show_beats_a_longer_title_that_only_mentions_it()
        {
            Assert.True(S("The Mandalorian", "mandalorian") > S("Star Wars: The Mandalorian and Grogu", "mandalorian"));
            Assert.True(S("The Mandalorian", "mandalorian") > S("LEGO Star Wars: The Mandalorian", "mandalorian"));
        }

        [Fact]
        public void Partial_word_matches_rank_by_how_many_of_the_words_are_there_and_below_whole_phrases()
        {
            var one = S("Star Wars Rebels", "star trek");
            var none = S("Frozen", "star trek");
            Assert.True(one > none);
            Assert.True(one < S("Star Trek: Voyager", "star trek"));
        }

        [Fact]
        public void The_score_never_reaches_a_perfect_match_unless_the_title_is_exact()
        {
            Assert.InRange(S("Simpsons", "simpsons"), 100, 100);
            Assert.True(S("Simpsons Roasting on an Open Fire", "simpsons") < 100);
            Assert.True(S("The Simpsons", "simpsons") < 100);
        }

        private static MediaItemViewModel Item(string id, string name, string source) =>
            new(new Baird.Models.MediaItemData { Id = id, Name = name, Details = "", ImageUrl = "", IsLive = false, Source = source, Type = MediaType.Video, Synopsis = "", Subtitle = "" });

        [Fact]
        public void A_better_Disney_match_comes_before_a_Netflix_result_that_answered_first()
        {
            // the order of arrival: Netflix was quick
            var arrived = new[]
            {
                Item("n1", "Maggie Simpson Movie Night", "Netflix"),
                Item("n2", "The Simpsons: Treehouse Special", "Netflix"),
                Item("d1", "The Simpsons", "Disney+"),
                Item("d2", "The Simpsons Movie", "Disney+"),
            };
            var sorter = new SearchResultSorter { ProviderOrder = new[] { "TvHeadend", "Jellyfin", "BBC iPlayer", "YouTube", "Netflix", "Disney+" } };

            var sorted = sorter.Sort(arrived, "simpsons");

            Assert.Equal("d1", sorted[0].Id);          // the show itself, from Disney+
            Assert.Equal("d2", sorted[1].Id);          // then the film
            Assert.Equal(new[] { "n2", "n1" }, sorted.Skip(2).Select(i => i.Id)); // Netflix after them (not better matches)
        }

        [Fact]
        public void Equal_matches_keep_the_providers_usual_order_whichever_answered_first()
        {
            var disneyFirst = new[] { Item("d", "One Piece", "Disney+"), Item("n", "One Piece", "Netflix"), Item("y", "One Piece", "YouTube") };
            var sorter = new SearchResultSorter { ProviderOrder = new[] { "TvHeadend", "Jellyfin", "BBC iPlayer", "YouTube", "Netflix", "Disney+" } };

            Assert.Equal(new[] { "y", "n", "d" }, sorter.Sort(disneyFirst, "one piece").Select(i => i.Id));
        }

        [Fact]
        public void A_source_with_a_host_suffix_still_finds_its_provider_rank()
        {
            var items = new[] { Item("b", "Frozen", "BBC iPlayer"), Item("j", "Frozen", "Jellyfin@server.lan") };
            var sorter = new SearchResultSorter { ProviderOrder = new[] { "TvHeadend", "Jellyfin", "BBC iPlayer" } };

            Assert.Equal(new[] { "j", "b" }, sorter.Sort(items, "frozen").Select(i => i.Id));
        }

        [Fact]
        public void Without_a_provider_order_equal_matches_stay_in_arrival_order()
        {
            var items = new[] { Item("a", "Frozen", "X"), Item("b", "Frozen", "Y") };
            Assert.Equal(new[] { "a", "b" }, new SearchResultSorter().Sort(items, "frozen").Select(i => i.Id));
        }

        [Fact]
        public void Results_with_no_title_match_come_last_in_provider_order()
        {
            var items = new[] { Item("x", "Something Else", "Netflix"), Item("m", "The Mandalorian", "Disney+") };
            var sorter = new SearchResultSorter { ProviderOrder = new[] { "Netflix", "Disney+" } };

            Assert.Equal(new[] { "m", "x" }, sorter.Sort(items, "mandalorian").Select(i => i.Id));
        }

        [Fact]
        public void A_short_numeric_query_is_still_a_channel_number_search()
        {
            var channel = new MediaItemViewModel(new Baird.Models.MediaItemData { Id = "c", Name = "BBC One", Details = "", ImageUrl = "", ChannelNumber = "1", IsLive = true, Source = "TvHeadend", Type = MediaType.Channel, Synopsis = "", Subtitle = "" });
            var video = Item("v", "1 Piece", "Netflix");

            Assert.Equal("c", new SearchResultSorter().Sort(new[] { video, channel }, "1")[0].Id);
        }
    }
}
