using Avalonia.Headless.XUnit;
using Baird.Services;
using Baird.ViewModels;
using Xunit;

namespace Baird.Tests.ViewModels
{
    public class MainViewModelNetflixTests
    {
        [AvaloniaFact]
        public void PlayItem_NetflixItem_ActivatesItLikeAnyVideo()
        {
            var vm = AppTestObject.Create().ViewModel;
            var item = new MediaItemViewModel(NetflixProvider.ToMediaItem(new NetflixSearchResult("Tenet", "80107103", null, "Movie")));

            vm.PlayItem(item);

            Assert.Same(item, vm.ActiveItem);
            Assert.IsType<ShowingVideoPlayerViewModel>(vm.CurrentPage);
        }

        [AvaloniaFact]
        public void PlayItem_NetflixSeries_OpensItsPageInsteadOfPlaying()
        {
            var vm = AppTestObject.Create().ViewModel;
            var item = new MediaItemViewModel(NetflixProvider.ToMediaItem(new NetflixSearchResult("One Piece", "80107103", null, "Show")));

            vm.PlayItem(item);

            Assert.IsType<ProgrammeDetailViewModel>(vm.CurrentPage);
            Assert.Null(vm.ActiveItem);
        }
    }
}
