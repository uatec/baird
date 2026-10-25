using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Baird.Services;

namespace Baird.ViewModels
{
    public class SearchResultSorter
    {
        /// <summary>
        /// The providers' names in their usual order (TvHeadend, Jellyfin, ...): the tie-break between equally good matches, so the
        /// order does not depend on which provider answered first. Items of a source not named here come after, in arrival order.
        /// </summary>
        public IReadOnlyList<string> ProviderOrder { get; init; } = Array.Empty<string>();

        public List<MediaItemViewModel> Sort(IEnumerable<MediaItemViewModel> items, string query)
        {
            var q = query ?? "";
            var allResults = items.ToList();

            bool isNumericShort = !string.IsNullOrEmpty(q) && q.Length <= 3 && q.All(char.IsDigit);

            if (isNumericShort)
            {
                var priority = new List<MediaItemViewModel>();
                var others = new List<MediaItemViewModel>();

                foreach (var item in allResults)
                {
                    bool isMatch = item.IsLive &&
                                   item.ChannelNumber != null &&
                                   (item.ChannelNumber == q || item.ChannelNumber.StartsWith(q));

                    if (isMatch)
                    {
                        priority.Add(item);
                    }
                    else
                    {
                        others.Add(item);
                    }
                }

                return priority.Concat(others).ToList();
            }
            else
            {
                // A text query: the best title match first, whichever provider answered first. Equal matches keep the providers' usual
                // order, then the order they arrived in (the sort is stable).
                int ProviderRank(MediaItemViewModel item)
                {
                    for (var i = 0; i < ProviderOrder.Count; i++)
                        if (item.Source.StartsWith(ProviderOrder[i], StringComparison.OrdinalIgnoreCase)) return i;
                    return int.MaxValue;
                }

                return allResults
                    .Select((item, index) => (item, index, score: SearchRelevance.Score(item.Name, q), rank: ProviderRank(item)))
                    .OrderByDescending(x => x.score)
                    .ThenBy(x => x.rank)
                    .ThenBy(x => x.index)
                    .Select(x => x.item)
                    .ToList();
            }
        }
    }
}
