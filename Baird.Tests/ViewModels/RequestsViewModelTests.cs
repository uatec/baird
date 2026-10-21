using System.Linq;
using Baird.Models;
using Baird.Services;
using Baird.ViewModels;
using Xunit;

namespace Baird.Tests.ViewModels
{
    public class RequestsViewModelTests
    {
        private static MediaItemViewModel Item(string name, string source) => new(new MediaItemData
        {
            Id = name + source,
            Name = name,
            Details = "",
            ImageUrl = "",
            IsLive = false,
            Source = source,
            Type = MediaType.Video,
            Synopsis = "",
            Subtitle = "",
        });

        [Fact]
        public void PickJellyfinMatch_IgnoresOtherProvidersWithTheSameTitle()
        {
            var results = new[]
            {
                Item("Hunted", "YouTube"),
                Item("Hunted", "BBC iPlayer"),
                Item("Hunted", "Jellyfin: jellyfin.neutrino.io"),
            };

            var match = RequestsViewModel.PickJellyfinMatch(results, "Hunted");

            Assert.Equal("Jellyfin: jellyfin.neutrino.io", match?.Source);
        }

        [Fact]
        public void PickJellyfinMatch_PrefersExactTitle_ThenFirstJellyfinResult()
        {
            var results = new[]
            {
                Item("The Traitors (US)", "Jellyfin: host"),
                Item("The Traitors", "Jellyfin: host"),
            };
            Assert.Equal("The Traitors", RequestsViewModel.PickJellyfinMatch(results, "the traitors")?.Name);
            Assert.Equal("The Traitors (US)", RequestsViewModel.PickJellyfinMatch(results.Take(1), "The Traitors")?.Name);
        }

        [Fact]
        public void PickJellyfinMatch_ReturnsNullWhenOnlyOtherProvidersMatch()
        {
            Assert.Null(RequestsViewModel.PickJellyfinMatch(new[] { Item("Hunted", "YouTube") }, "Hunted"));
        }
    }
}
