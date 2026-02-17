using ReactiveUI;
using System;
using System.Reactive;
using System.Threading.Tasks;
using Baird.Services;

namespace Baird.ViewModels
{
    public class CecDebugViewModel : ReactiveObject
    {
        private readonly ICecService _cecService;
        private string _statusText = "Ready";
        private bool _isExecuting = false;

        public event EventHandler? BackRequested;

        public string StatusText
        {
            get => _statusText;
            set => this.RaiseAndSetIfChanged(ref _statusText, value);
        }

        public bool IsExecuting
        {
            get => _isExecuting;
            set => this.RaiseAndSetIfChanged(ref _isExecuting, value);
        }

        public ReactiveCommand<Unit, Unit> PowerOnCommand { get; }
        public ReactiveCommand<Unit, Unit> PowerOffCommand { get; }
        public ReactiveCommand<Unit, Unit> TogglePowerCommand { get; }
        public ReactiveCommand<Unit, Unit> VolumeUpCommand { get; }
        public ReactiveCommand<Unit, Unit> VolumeDownCommand { get; }
        public ReactiveCommand<Unit, Unit> ChangeInputCommand { get; }
        public ReactiveCommand<Unit, Unit> CycleInputsCommand { get; }
        public ReactiveCommand<Unit, Unit> RefreshStatusCommand { get; }
        public ReactiveCommand<Unit, Unit> BackCommand { get; }

        public CecDebugViewModel(ICecService cecService)
        {
            _cecService = cecService ?? throw new ArgumentNullException(nameof(cecService));

            // Create commands
            PowerOnCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                await ExecuteWithStatus("Sending Power On...", _cecService.PowerOnAsync);
            }, this.WhenAnyValue(x => x.IsExecuting, executing => !executing));

            PowerOffCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                await ExecuteWithStatus("Sending Power Off...", _cecService.PowerOffAsync);
            }, this.WhenAnyValue(x => x.IsExecuting, executing => !executing));

            TogglePowerCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                await ExecuteWithStatus("Toggling Power...", _cecService.TogglePowerAsync);
            }, this.WhenAnyValue(x => x.IsExecuting, executing => !executing));

            VolumeUpCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                await ExecuteWithStatus("Volume Up", _cecService.VolumeUpAsync);
            }, this.WhenAnyValue(x => x.IsExecuting, executing => !executing));

            VolumeDownCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                await ExecuteWithStatus("Volume Down", _cecService.VolumeDownAsync);
            }, this.WhenAnyValue(x => x.IsExecuting, executing => !executing));

            ChangeInputCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                await ExecuteWithStatus("Changing Input to This Device...", _cecService.ChangeInputToThisDeviceAsync);
            }, this.WhenAnyValue(x => x.IsExecuting, executing => !executing));

            CycleInputsCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                await ExecuteWithStatus("Cycling Inputs...", _cecService.CycleInputsAsync);
            }, this.WhenAnyValue(x => x.IsExecuting, executing => !executing));

            RefreshStatusCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                IsExecuting = true;
                try
                {
                    StatusText = "Getting Power Status...";
                    var status = await _cecService.GetPowerStatusAsync();
                    StatusText = $"Power Status: {status}";
                }
                catch (Exception ex)
                {
                    StatusText = $"Error: {ex.Message}";
                }
                finally
                {
                    IsExecuting = false;
                }
            }, this.WhenAnyValue(x => x.IsExecuting, executing => !executing));

            BackCommand = ReactiveCommand.Create(() =>
            {
                BackRequested?.Invoke(this, EventArgs.Empty);
            });

            // Get initial status
            RefreshStatus();
        }

        private async void RefreshStatus()
        {
            try
            {
                var status = await _cecService.GetPowerStatusAsync();
                StatusText = $"Power Status: {status}";
            }
            catch (Exception ex)
            {
                StatusText = $"Error getting status: {ex.Message}";
            }
        }

        private async Task ExecuteWithStatus(string message, Func<Task> action)
        {
            IsExecuting = true;
            try
            {
                StatusText = message;
                await action();
                StatusText = $"{message} - Complete";

                // Refresh power status after commands
                await Task.Delay(500); // Brief delay to allow command to process
                var status = await _cecService.GetPowerStatusAsync();
                StatusText = $"Power Status: {status}";
            }
            catch (Exception ex)
            {
                StatusText = $"Error: {ex.Message}";
            }
            finally
            {
                IsExecuting = false;
            }
        }
    }
}