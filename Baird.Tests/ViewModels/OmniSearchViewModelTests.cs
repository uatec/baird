using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Baird.Services;
using Baird.Tests.Mocks;
using Baird.ViewModels;
using Xunit;

namespace Baird.Tests.ViewModels
{
    public class OmniSearchViewModelTests
    {
        [Fact]
        public async Task Search_NumericUnder3Digits_PrioritizesLiveChannels_And_PreservesProviderOrder()
        {
            // Arrange
            // Provider 1: "VOD 1" (Not Live), "BBC One" (Live, Ch 1), "Channel 4" (Live, Ch 104)
            // Provider 2: "RTE One" (Live, Ch 1), "Video 1" (Not Live)
            
            var provider1Items = new List<MediaItem>
            {
                new MediaItem { Name = "VOD 1", IsLive = false, Source = "P1" },
                new MediaItem { Name = "BBC One", ChannelNumber = "1", IsLive = true, Source = "P1" },
                new MediaItem { Name = "Channel 4", ChannelNumber = "104", IsLive = true, Source = "P1" }
            };

            var provider2Items = new List<MediaItem>
            {
                new MediaItem { Name = "RTE One", ChannelNumber = "1", IsLive = true, Source = "P2" },
                new MediaItem { Name = "Video 1", IsLive = false, Source = "P2" }
            };

            var p1 = new MockMediaProvider("P1", provider1Items);
            var p2 = new MockMediaProvider("P2", provider2Items);

            var sorter = new SearchResultSorter();

            // Act
            var results = await sorter.SearchAndSortAsync(new[] { p1, p2 }, "1");

            // Assert
            // Expected Order:
            // Priority (Live + Ch "1" or starts with "1"):
            // 1. BBC One (P1, Ch 1)
            // 2. RTE One (P2, Ch 1)
            // Others (Provider order preserved):
            // 3. VOD 1 (P1)
            // 4. Channel 4 (P1, Ch 104) - Matches "1" in "104" but logic says "starts with". 
            //    Wait, logic checking: `isMatch = ... StartsWith(q)`.
            //    "104" starts with "1". So it IS a priority match if IsLive.
            //    So Channel 4 IS a priority match.
            //    
            //    So Priority should be:
            //    - BBC One (P1)
            //    - Channel 4 (P1)
            //    - RTE One (P2)
            //    
            //    Others:
            //    - VOD 1 (P1)
            //    - Video 1 (P2)
            //
            //    Wait! `allResults` is P1[VOD, BBC, Ch4], P2[RTE, Vid].
            //    Iterating `allResults`:
            //    1. VOD 1 -> Not Live -> Others
            //    2. BBC One -> Live, Ch "1" starts with "1" -> Priority
            //    3. Channel 4 -> Live, Ch "104" starts with "1" -> Priority
            //    4. RTE One -> Live, Ch "1" starts with "1" -> Priority
            //    5. Video 1 -> Not Live -> Others
            //
            //    Concat(Priority, Others)
            //    Priority: [BBC One, Channel 4, RTE One]
            //    Others: [VOD 1, Video 1]
            //    
            //    Total: [BBC One, Channel 4, RTE One, VOD 1, Video 1]
            
            Assert.Equal(5, results.Count);
            Assert.Equal("BBC One", results[0].Name);
            Assert.Equal("Channel 4", results[1].Name);
            Assert.Equal("RTE One", results[2].Name);
            Assert.Equal("VOD 1", results[3].Name);
            Assert.Equal("Video 1", results[4].Name);
        }

        [Fact]
        public async Task Search_NonNumeric_PreservesProviderOrder()
        {
             // Arrange
            var provider1Items = new List<MediaItem>
            {
                new MediaItem { Name = "Alpha", Source = "P1" },
                new MediaItem { Name = "Charlie", Source = "P1" }
            };

            var provider2Items = new List<MediaItem>
            {
                new MediaItem { Name = "Bravo", Source = "P2" },
                new MediaItem { Name = "Delta", Source = "P2" }
            };

            var p1 = new MockMediaProvider("P1", provider1Items);
            var p2 = new MockMediaProvider("P2", provider2Items);
            
            var sorter = new SearchResultSorter();

            // Act
            var results = await sorter.SearchAndSortAsync(new[] { p1, p2 }, "a");

            // Assert
            // Should be P1 items then P2 items (Alpha, Charlie, Bravo, Delta) assuming "a" matches all (contains 'a')
            // "Alpha" (matches), "Charlie" (matches), "Bravo" (matches), "Delta" (matches)
            
            Assert.Equal("Alpha", results[0].Name);
            Assert.Equal("Charlie", results[1].Name);
            Assert.Equal("Bravo", results[2].Name);
            Assert.Equal("Delta", results[3].Name);
        }
    }
}
