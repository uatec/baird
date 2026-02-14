using System;
using System.Collections.Generic;
using Baird.ViewModels;
using ReactiveUI;
using Xunit;

namespace Baird.Tests.ViewModels
{
    public class TabNavigationViewModelTests
    {
        [Fact]
        public void Constructor_WithTabs_ShouldSelectFirstTab()
        {
            // Arrange
            var content1 = new TestViewModel { Name = "Content 1" };
            var tabs = new[]
            {
                new TabItem("Tab 1", content1)
            };

            // Act
            var tabNav = new TabNavigationViewModel(tabs);

            // Assert
            Assert.Single(tabNav.Tabs);
            Assert.Equal(0, tabNav.SelectedIndex);
            Assert.NotNull(tabNav.SelectedTab);
            Assert.Equal("Tab 1", tabNav.SelectedTab.Title);
        }

        [Fact]
        public void Constructor_WithMultipleTabs_ShouldSelectFirstTab()
        {
            // Arrange
            var content1 = new TestViewModel { Name = "Content 1" };
            var content2 = new TestViewModel { Name = "Content 2" };
            var tabs = new[]
            {
                new TabItem("Tab 1", content1),
                new TabItem("Tab 2", content2)
            };

            // Act
            var tabNav = new TabNavigationViewModel(tabs);

            // Assert
            Assert.Equal(2, tabNav.Tabs.Count);
            Assert.Equal(0, tabNav.SelectedIndex);
            Assert.Equal("Tab 1", tabNav.SelectedTab?.Title);
        }

        [Fact]
        public void SelectTab_ByChangingIndex_ShouldUpdateSelectedTab()
        {
            // Arrange
            var content1 = new TestViewModel { Name = "Content 1" };
            var content2 = new TestViewModel { Name = "Content 2" };
            var tabs = new[]
            {
                new TabItem("Tab 1", content1),
                new TabItem("Tab 2", content2)
            };
            var tabNav = new TabNavigationViewModel(tabs);

            // Act
            tabNav.SelectedIndex = 1;

            // Assert
            Assert.Equal(1, tabNav.SelectedIndex);
            Assert.Equal("Tab 2", tabNav.SelectedTab?.Title);
        }

        [Fact]
        public void SelectTabCommand_ShouldUpdateSelectedTab()
        {
            // Arrange
            var content1 = new TestViewModel { Name = "Content 1" };
            var content2 = new TestViewModel { Name = "Content 2" };
            var tabs = new[]
            {
                new TabItem("Tab 1", content1),
                new TabItem("Tab 2", content2)
            };
            var tabNav = new TabNavigationViewModel(tabs);
            var tab2 = tabNav.Tabs[1];

            // Act
            tabNav.SelectTabCommand.Execute(tab2).Subscribe();

            // Assert
            Assert.Equal(1, tabNav.SelectedIndex);
            Assert.Equal("Tab 2", tabNav.SelectedTab?.Title);
        }

        [Fact]
        public void BackCommand_ShouldRaiseBackRequestedEvent()
        {
            // Arrange
            var tabs = new[]
            {
                new TabItem("Tab 1", new TestViewModel())
            };
            var tabNav = new TabNavigationViewModel(tabs);
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
