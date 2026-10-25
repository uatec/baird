using System.Linq;
using Baird.ViewModels;
using Xunit;

namespace Baird.Tests.ViewModels
{
    public class HomeNavigationTests
    {
        [Fact]
        public void Home_opens_the_History_tab_from_another_tab_and_Back_returns_to_the_video()
        {
            var vm = AppTestObject.Create().ViewModel;
            vm.OpenMainMenu(vm.MainMenu.Tabs.ToList().FindIndex(t => t.Title == "Search"));

            vm.GoToHistory();

            Assert.Same(vm.MainMenu, vm.CurrentPage);
            Assert.Equal("History", vm.MainMenu.SelectedTab?.Title);

            vm.GoBack();

            Assert.True(vm.IsVideoPlayerActive);
        }
    }
}
