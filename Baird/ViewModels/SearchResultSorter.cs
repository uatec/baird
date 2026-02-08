using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Baird.Services;

namespace Baird.ViewModels
{
    public class SearchResultSorter
    {
        public async Task<List<MediaItem>> SearchAndSortAsync(IEnumerable<IMediaProvider> providers, string query)
        {
            var q = query ?? "";
            
            // Execute searches in parallel (or sequentially, but keeping provider order in result processing is key)
            // Even if we run in parallel, we must concat results in provider order.
            
            // To preserve order: 
            // 1. Start all tasks
            // 2. Wait for all
            // 3. Concat in original provider order
            
            var searchTasks = providers.Select(async p => await p.SearchAsync(q)).ToList();
            var searchResults = await Task.WhenAll(searchTasks);
            
            var allResults = searchResults.SelectMany(r => r ?? Enumerable.Empty<MediaItem>()).ToList();
            
            bool isNumericShort = !string.IsNullOrEmpty(q) && q.Length <= 3 && q.All(char.IsDigit);
            
            if (isNumericShort)
            {
                // Prioritize: Live TV with Channel Number matching query
                // We want these to bubble to the top, but WITHIN that group, preserve provider order/internal order?
                // And for the rest, preserve provider order.
                
                // Requirement: "If the search term is entirely numeric and less than 3 digits... match live streams with channel numbers should match first."
                // "after that results should be returned in the order they were returned from the provider."
                // "Results from different providers should be ordered by the order of the providers themselves."
                
                // Group A: Priority Matches (Live + ChannelNum matches query)
                // Group B: Everything else
                
                // We need to implement stable sorting or explicit grouping to preserve relative order.
                
                var priority = new List<MediaItem>();
                var others = new List<MediaItem>();

                foreach (var item in allResults)
                {
                    bool isMatch = item.IsLive && 
                                   item.ChannelNumber != null && 
                                   (item.ChannelNumber == q || item.ChannelNumber.StartsWith(q)); 
                                   // Note: User said "match live streams with channel numbers should match first".
                                   // "StartsWith" is usually good for "1" matching "1", "10", "100".
                                   // But if query is "1", "1" is a better match than "10". 
                                   // The prompt says "match ... first".
                                   // Let's assume StartsWith is acceptable for "matching", but exact match is best?
                                   // For now, let's treat "Starts With" as the criteria for "matching" in this context 
                                   // based on typical channel entry (typing '1' to see '1', '10', '11', '12').
                    
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
                // Default: Just provider order, no re-sorting by name/etc unless provider did it.
                // The `SelectMany` on the array of results (which corresponds to `providers` array order) preserves provider order.
                return allResults;
            }
        }
    }
}
