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
        public void Search_NumericUnder3Digits_PrioritizesLiveChannels_And_PreservesProviderOrder()
        {
            // Arrange
            // Provider 1: "VOD 1" (Not Live), "BBC One" (Live, Ch 1), "Channel 4" (Live, Ch 104)
            // Provider 2: "RTE One" (Live, Ch 1), "Video 1" (Not Live)

            var provider1Items = new List<MediaItemViewModel>
            {
                new MediaItemViewModel(new MediaItemData { Id = "1", Name = "VOD 1", Details = "", ImageUrl = "", IsLive = false, Source = "P1", Type = MediaType.Video, Synopsis = "", Subtitle = "" }),
                new MediaItemViewModel(new MediaItemData { Id = "2", Name = "BBC One", Details = "", ImageUrl = "", ChannelNumber = "1", IsLive = true, Source = "P1", Type = MediaType.Channel, Synopsis = "", Subtitle = "" }),
                new MediaItemViewModel(new MediaItemData { Id = "3", Name = "Channel 4", Details = "", ImageUrl = "", ChannelNumber = "104", IsLive = true, Source = "P1", Type = MediaType.Channel, Synopsis = "", Subtitle = "" })
            };

            var provider2Items = new List<MediaItemViewModel>
            {
                new MediaItemViewModel(new MediaItemData { Id = "4", Name = "RTE One", Details = "", ImageUrl = "", ChannelNumber = "1", IsLive = true, Source = "P2", Type = MediaType.Channel, Synopsis = "", Subtitle = "" }),
                new MediaItemViewModel(new MediaItemData { Id = "5", Name = "Video 1", Details = "", ImageUrl = "", IsLive = false, Source = "P2", Type = MediaType.Video, Synopsis = "", Subtitle = "" })
            };

            // Combine as if search results came in
            var allItems = provider1Items.Concat(provider2Items);

            var sorter = new SearchResultSorter();

            // Act
            var results = sorter.Sort(allItems, "1");

            // Assert
            Assert.Equal(5, results.Count);
            // BBC One (Live, Ch 1) -> Priority
            // RTE One (Live, Ch 1) -> Priority. (Wait, which one first? Stable sort? The code does: foreach item, if match add to priority. So preserves order of appearance.)
            // Channel 4 (Live, Ch 104) -> Priority?
            // "1" matches "104".StartsWith("1") -> Yes.

            // Expected Priority List: 
            // 1. BBC One (P1)
            // 2. Channel 4 (P1)
            // 3. RTE One (P2)

            // Expected Others List:
            // 4. VOD 1 (P1)
            // 5. Video 1 (P2)

            Assert.Equal("BBC One", results[0].Name);
            Assert.Equal("Channel 4", results[1].Name);
            Assert.Equal("RTE One", results[2].Name);
            Assert.Equal("VOD 1", results[3].Name);
            Assert.Equal("Video 1", results[4].Name);
        }

        [Fact]
        public void Search_NonNumeric_PreservesProviderOrder()
        {
            // Arrange
            var provider1Items = new List<MediaItemViewModel>
            {
                new MediaItemViewModel(new MediaItemData { Id = "1", Name = "Alpha", Details = "", ImageUrl = "", IsLive = false, Source = "P1", Type = MediaType.Video, Synopsis = "", Subtitle = "" }),
                new MediaItemViewModel(new MediaItemData { Id = "2", Name = "Charlie", Details = "", ImageUrl = "", IsLive = false, Source = "P1", Type = MediaType.Video, Synopsis = "", Subtitle = "" })
            };

            var provider2Items = new List<MediaItemViewModel>
            {
                new MediaItemViewModel(new MediaItemData { Id = "3", Name = "Bravo", Details = "", ImageUrl = "", IsLive = false, Source = "P2", Type = MediaType.Video, Synopsis = "", Subtitle = "" }),
                new MediaItemViewModel(new MediaItemData { Id = "4", Name = "Delta", Details = "", ImageUrl = "", IsLive = false, Source = "P2", Type = MediaType.Video, Synopsis = "", Subtitle = "" })
            };

            var allItems = provider1Items.Concat(provider2Items);

            var sorter = new SearchResultSorter();

            // Act
            var results = sorter.Sort(allItems, "a");

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
