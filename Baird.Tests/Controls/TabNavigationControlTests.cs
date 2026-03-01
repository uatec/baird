using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Baird.Controls;
using Baird.ViewModels;
using Xunit;

namespace Baird.Tests.Controls
{
    public class TabNavigationControlTests
    {
        [Fact]
        public void TabButton_KeyUp_ClosesMainMenu()
        {
            // Arrange
            var app = AppTestObject.Create();

            // Act: Open the main menu
            var mainMenu = app.OpenMainMenu();

            // Verify we're in the menu
            Assert.IsType<TabNavigationViewModel>(app.CurrentView);

            // Act: Press Up while focused on a tab button
            mainMenu.PressUpOnTab();

            // Assert: The menu should be closed, returning to Video Player (CurrentView == null)
            Assert.Null(app.CurrentView);
        }
    }
}
