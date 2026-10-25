using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Baird.ViewModels
{
    /// <summary>
    /// How well a result's title answers a text query, so that results from different providers can be ranked together instead of
    /// in the order the providers happened to answer (Netflix, being quick, used to come before a better Disney+ match). A higher
    /// score is a better match; 0 means the title does not contain the query (the provider matched on something else).
    /// Scale: exact title 100, exact after a leading "the"/"a"/"an" 96, title starts with the query 80, every query word is a whole
    /// word of the title 65, the query is a whole-word phrase inside the title 55, the query is inside a word 40, some of the
    /// query's words are in the title 10 to 30. Whole-word matches get up to 9 more points for a short title (closer to what was typed).
    /// </summary>
    public static class SearchRelevance
    {
        private static readonly string[] LeadingArticles = { "the", "a", "an" };

        public static double Score(string? title, string? query)
        {
            var t = Normalize(title);
            var q = Normalize(query);
            if (t.Length == 0 || q.Length == 0) return 0;

            double score;
            if (t == q) return 100;

            var tNoArticle = StripLeadingArticle(t);
            var qNoArticle = StripLeadingArticle(q);
            if (tNoArticle == qNoArticle) return 96;

            var qTokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var tTokens = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool Whole(string token) => tTokens.Contains(token);

            if (t.StartsWith(q + " ", StringComparison.Ordinal) || tNoArticle.StartsWith(qNoArticle + " ", StringComparison.Ordinal))
                score = 80;
            else if (t.StartsWith(q, StringComparison.Ordinal))
                score = 72; // the title starts with the query inside a word ("simpsons" for "simpsonsons")
            else if (qTokens.All(Whole))
                score = 65;
            else if ((" " + t + " ").Contains(" " + q + " ", StringComparison.Ordinal))
                score = 55;
            else if (t.Contains(q, StringComparison.Ordinal))
                score = 40;
            else
            {
                var present = qTokens.Count(tok => Whole(tok) || tTokens.Any(w => w.StartsWith(tok, StringComparison.Ordinal)));
                if (present == 0) return 0;
                score = 10 + 20.0 * present / qTokens.Length;
            }

            // A shorter title is closer to what was typed: "The Mandalorian" before "Star Wars: The Mandalorian and Grogu". Only for
            // whole-word matches: a hit inside a word (the letter "a") says nothing about how close the title is.
            if (score < 55) return score;
            var closeness = Math.Min(1.0, (double)q.Length / t.Length);
            return Math.Min(99, score + 9 * closeness);
        }

        /// <summary>Lower case, letters and digits only (accents removed), words separated by single spaces: "Spider-Man: No Way" = "spider man no way".</summary>
        public static string Normalize(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var decomposed = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            var lastWasSpace = true;
            foreach (var c in decomposed)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category == UnicodeCategory.NonSpacingMark) continue; // the accent
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(char.ToLowerInvariant(c));
                    lastWasSpace = false;
                }
                else if (c == '\'' || c == '’') continue; // "Grey's" = "greys", not "grey s"
                else if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
            }
            return sb.ToString().Trim();
        }

        private static string StripLeadingArticle(string normalized)
        {
            foreach (var article in LeadingArticles)
                if (normalized.StartsWith(article + " ", StringComparison.Ordinal)) return normalized[(article.Length + 1)..];
            return normalized;
        }
    }
}
