using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Baird.Services
{
    /// <summary>One result card on Netflix's search page, as read from the signed-in session.</summary>
    public sealed record NetflixSearchCard(string Title, string Href, string? ImageUrl);

    /// <summary>
    /// What the browser is showing while we try to reach search results. The search page of a signed-in session only lists
    /// titles in that account's catalogue (region and profile), so the results are already "filtered by availability".
    /// </summary>
    public sealed record NetflixSearchPage(
        string Url,
        bool SignedOut,
        IReadOnlyList<string> Profiles,
        IReadOnlyList<NetflixSearchCard> Cards);

    public sealed record NetflixSearchResult(string Title, string TitleId, string? ImageUrl)
    {
        public string Url => $"https://www.netflix.com/title/{TitleId}";
    }

    public static class NetflixSearchParser
    {
        // Search cards link to /search?q=...&jbv=<id>; other Netflix links use /title/<id> or /watch/<id>.
        private static readonly Regex IdPattern = new(@"(?:[?&]jbv=|/title/|/watch/)(\d+)", RegexOptions.Compiled);

        public static string? ParseTitleId(string? href)
        {
            if (string.IsNullOrEmpty(href)) return null;
            var m = IdPattern.Match(href);
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>Cards to results: drops cards without a title or id, and duplicates (Netflix repeats titles across rows).</summary>
        public static List<NetflixSearchResult> ToResults(IEnumerable<NetflixSearchCard> cards)
        {
            var seen = new HashSet<string>();
            var results = new List<NetflixSearchResult>();
            foreach (var card in cards)
            {
                var id = ParseTitleId(card.Href);
                if (id == null || string.IsNullOrWhiteSpace(card.Title) || !seen.Add(id)) continue;
                results.Add(new NetflixSearchResult(card.Title.Trim(), id, string.IsNullOrEmpty(card.ImageUrl) ? null : card.ImageUrl));
            }
            return results;
        }

        public static string SearchUrl(string term) => "https://www.netflix.com/search?q=" + Uri.EscapeDataString(term);
    }
}
