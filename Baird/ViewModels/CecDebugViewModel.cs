using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Threading.Tasks;
using Baird.Services;

namespace Baird.ViewModels
{
    public class CecLogEntryViewModel : ReactiveObject
    {
        public DateTime Timestamp { get; set; }
        public string Command { get; set; } = string.Empty;
        public string Response { get; set; } = string.Empty;
        public bool Success { get; set; }

        public string TimeText => Timestamp.ToString("HH:mm:ss");
        public string StatusText => Success ? "✓" : "✗";
        public string StatusColor => Success ? "#4CAF50" : "#F44336";
    }

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

        public ObservableCollection<CecLogEntryViewModel> LogEntries { get; } = new();

        public ReactiveCommand<Unit, Unit> PowerOnCommand { get; }
        public ReactiveCommand<Unit, Unit> PowerOffCommand { get; }
        public ReactiveCommand<Unit, Unit> TogglePowerCommand { get; }
        public ReactiveCommand<Unit, Unit> VolumeUpCommand { get; }
        public ReactiveCommand<Unit, Unit> VolumeDownCommand { get; }
        public ReactiveCommand<Unit, Unit> ChangeInputCommand { get; }
        public ReactiveCommand<Unit, Unit> CycleInputsCommand { get; }
        public ReactiveCommand<Unit, Unit> RefreshStatusCommand { get; }
        public ReactiveCommand<Unit, Unit> ClearLogCommand { get; }
        public ReactiveCommand<Unit, Unit> BackCommand { get; }

        public CecDebugViewModel(ICecService cecService)
        {
            _cecService = cecService ?? throw new ArgumentNullException(nameof(cecService));

            // Subscribe to command logging
            _cecService.CommandLogged += OnCommandLogged;

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

            ClearLogCommand = ReactiveCommand.Create(() =>
            {
                LogEntries.Clear();
            });

            BackCommand = ReactiveCommand.Create(() =>
            {
                _cecService.CommandLogged -= OnCommandLogged;
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

        private void OnCommandLogged(object? sender, CecCommandLoggedEventArgs e)
        {
            var logEntry = new CecLogEntryViewModel
            {
                Timestamp = e.Timestamp,
                Command = e.Command,
                Response = e.Response,
                Success = e.Success
            };

            // Add to log on UI thread
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                LogEntries.Insert(0, logEntry); // Add to beginning so newest is on top
                
                // Keep only last 50 entries to avoid memory issues
                while (LogEntries.Count > 50)
                {
                    LogEntries.RemoveAt(LogEntries.Count - 1);
                }
            });
        }
    }
}