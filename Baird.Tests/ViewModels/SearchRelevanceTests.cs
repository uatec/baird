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

    }
}
