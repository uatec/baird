using System;
using Baird.ViewModels;
using Xunit;

namespace Baird.Tests.Controls
{
    public class SettingsControlTests
    {
        [Fact]
        public void SettingsViewModel_SimulateKeyCommands_Decrease_Adjusts_Scale()
        {
            // Simulate the interaction from the Control's KeyDown event
            // that invokes the ViewModel command. This verifies the control logic 
            // without requiring the Avalonia XAML loader to parse the actual UI.

            var viewModel = new SettingsViewModel(null);
            Assert.Equal(1.0, viewModel.UiScale);

            viewModel.DecreaseScaleCommand.Execute().Subscribe();

            Assert.Equal(0.9, viewModel.UiScale);
        }

        [Fact]
        public void SettingsViewModel_SimulateKeyCommands_Increase_Adjusts_Scale()
        {
            var viewModel = new SettingsViewModel(null);
            viewModel.IncreaseScaleCommand.Execute().Subscribe();

            Assert.Equal(1.1, viewModel.UiScale);
        }
    }
}
