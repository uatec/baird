using System;
using Baird.ViewModels;
using ReactiveUI;
using Xunit;

namespace Baird.Tests.ViewModels
{
    public class TabNavigationViewModelTests
    {
        [Fact]
        public void AddTab_FirstTab_ShouldBeSelected()
        {
            // Arrange
            var tabNav = new TabNavigationViewModel();
            var content1 = new TestViewModel { Name = "Content 1" };

            // Act
            tabNav.AddTab("Tab 1", content1);

            // Assert
            Assert.Single(tabNav.Tabs);
            Assert.Equal(0, tabNav.SelectedIndex);
            Assert.NotNull(tabNav.SelectedTab);
            Assert.Equal("Tab 1", tabNav.SelectedTab.Title);
        }

        [Fact]
        public void AddTab_MultipleTabs_FirstShouldRemainSelected()
        {
            // Arrange
            var tabNav = new TabNavigationViewModel();
            var content1 = new TestViewModel { Name = "Content 1" };
            var content2 = new TestViewModel { Name = "Content 2" };

            // Act
            tabNav.AddTab("Tab 1", content1);
            tabNav.AddTab("Tab 2", content2);

            // Assert
            Assert.Equal(2, tabNav.Tabs.Count);
            Assert.Equal(0, tabNav.SelectedIndex);
            Assert.Equal("Tab 1", tabNav.SelectedTab?.Title);
        }

        [Fact]
        public void SelectTab_ValidIndex_ShouldUpdateSelectedTab()
        {
            // Arrange
            var tabNav = new TabNavigationViewModel();
            var content1 = new TestViewModel { Name = "Content 1" };
            var content2 = new TestViewModel { Name = "Content 2" };
            tabNav.AddTab("Tab 1", content1);
            tabNav.AddTab("Tab 2", content2);

            // Act
            tabNav.SelectTab(1);

            // Assert
            Assert.Equal(1, tabNav.SelectedIndex);
            Assert.Equal("Tab 2", tabNav.SelectedTab?.Title);
        }

        [Fact]
        public void NextTabCommand_ShouldCycleToNextTab()
        {
            // Arrange
            var tabNav = new TabNavigationViewModel();
            tabNav.AddTab("Tab 1", new TestViewModel());
            tabNav.AddTab("Tab 2", new TestViewModel());
            tabNav.AddTab("Tab 3", new TestViewModel());
            tabNav.SelectedIndex = 0;

            // Act
            tabNav.NextTabCommand.Execute().Subscribe();

            // Assert
            Assert.Equal(1, tabNav.SelectedIndex);
        }

        [Fact]
        public void NextTabCommand_AtLastTab_ShouldWrapToFirst()
        {
            // Arrange
            var tabNav = new TabNavigationViewModel();
            tabNav.AddTab("Tab 1", new TestViewModel());
            tabNav.AddTab("Tab 2", new TestViewModel());
            tabNav.SelectedIndex = 1; // Last tab

            // Act
            tabNav.NextTabCommand.Execute().Subscribe();

            // Assert
            Assert.Equal(0, tabNav.SelectedIndex);
        }

        [Fact]
        public void PreviousTabCommand_ShouldCycleToPreviousTab()
        {
            // Arrange
            var tabNav = new TabNavigationViewModel();
            tabNav.AddTab("Tab 1", new TestViewModel());
            tabNav.AddTab("Tab 2", new TestViewModel());
            tabNav.SelectedIndex = 1;

            // Act
            tabNav.PreviousTabCommand.Execute().Subscribe();

            // Assert
            Assert.Equal(0, tabNav.SelectedIndex);
        }

        [Fact]
        public void PreviousTabCommand_AtFirstTab_ShouldWrapToLast()
        {
            // Arrange
            var tabNav = new TabNavigationViewModel();
            tabNav.AddTab("Tab 1", new TestViewModel());
            tabNav.AddTab("Tab 2", new TestViewModel());
            tabNav.SelectedIndex = 0; // First tab

            // Act
            tabNav.PreviousTabCommand.Execute().Subscribe();

            // Assert
            Assert.Equal(1, tabNav.SelectedIndex);
        }

        [Fact]
        public void BackCommand_ShouldRaiseBackRequestedEvent()
        {
            // Arrange
            var tabNav = new TabNavigationViewModel();
            var eventRaised = false;
            tabNav.BackRequested += (s, e) => eventRaised = true;

            // Act
            tabNav.BackCommand.Execute().Subscribe();

            // Assert
            Assert.True(eventRaised);
        }

        // Simple test view model for testing
        private class TestViewModel : ReactiveObject
        {
            private string _name = string.Empty;
            public string Name
            {
                get => _name;
                set => this.RaiseAndSetIfChanged(ref _name, value);
            }
        }
    }
}
