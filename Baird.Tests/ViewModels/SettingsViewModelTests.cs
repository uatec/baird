using System;
using Baird.ViewModels;
using Xunit;

namespace Baird.Tests.ViewModels
{
    public class SettingsViewModelTests
    {
        [Fact]
        public void Default_UiScale_IsOne()
        {
            var vm = new SettingsViewModel();
            Assert.Equal(1.0, vm.UiScale);
        }

        [Fact]
        public void IncreaseScaleCommand_IncrementsScale()
        {
            var vm = new SettingsViewModel();
            vm.IncreaseScaleCommand.Execute().Subscribe();

            Assert.Equal(1.1, vm.UiScale);
        }

        [Fact]
        public void DecreaseScaleCommand_DecrementsScale()
        {
            var vm = new SettingsViewModel();
            vm.DecreaseScaleCommand.Execute().Subscribe();

            Assert.Equal(0.9, vm.UiScale);
        }

        [Fact]
        public void IncreaseScaleCommand_ObeysMaximumBounds()
        {
            var vm = new SettingsViewModel();

            // Increment well past maximum (2.0)
            for (int i = 0; i < 20; i++)
            {
                vm.IncreaseScaleCommand.Execute().Subscribe();
            }

            Assert.Equal(2.0, vm.UiScale);
        }

        [Fact]
        public void DecreaseScaleCommand_ObeysMinimumBounds()
        {
            var vm = new SettingsViewModel();

            // Decrement well past minimum (0.5)
            for (int i = 0; i < 20; i++)
            {
                vm.DecreaseScaleCommand.Execute().Subscribe();
            }

            Assert.Equal(0.5, vm.UiScale);
        }
    }
}
