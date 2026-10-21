using Avalonia.Headless.XUnit;
using Baird.Controls;
using Baird.ViewModels;
using Xunit;

namespace Baird.Tests.Controls
{
    public class LottieSpikeControlTests
    {
        [AvaloniaFact]
        public void LottieSpikeControl_InstantiatesAndBindsViewModel()
        {
            var vm = new LottieSpikeViewModel();
            var control = new LottieSpikeControl
            {
                DataContext = vm
            };

            Assert.NotNull(control);
            Assert.Same(vm, control.DataContext);
        }

        [AvaloniaFact]
        public void LottieSpikeControl_PlaybackRequests_DoNotThrowWhenNotAttached()
        {
            var vm = new LottieSpikeViewModel();
            var control = new LottieSpikeControl
            {
                DataContext = vm
            };

            // Triggering viewmodel playback actions when not yet attached to visual tree should be handled gracefully
            vm.Play();
            vm.Pause();
            vm.Restart();
            vm.SeekCommand.Execute(0.5).Subscribe();
        }
    }
}
