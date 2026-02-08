using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Baird.Services;

namespace Baird.ViewModels
{
    public class SearchResultSorter
    {
        public List<MediaItem> Sort(IEnumerable<MediaItem> items, string query)
        {
            var q = query ?? "";
            var allResults = items.ToList();
            
            bool isNumericShort = !string.IsNullOrEmpty(q) && q.Length <= 3 && q.All(char.IsDigit);
            
            if (isNumericShort)
            {
                var priority = new List<MediaItem>();
                var others = new List<MediaItem>();

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
                return allResults;
            }
        }
    }
}
