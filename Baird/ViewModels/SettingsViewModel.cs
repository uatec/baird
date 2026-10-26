using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Reactive;
using Microsoft.Extensions.Configuration;
using System.IO;

namespace Baird.ViewModels
{
    public class SettingsViewModel : ReactiveObject
    {
        private double _uiScale = 1.0;

        // Boundaries for scaling
        private const double MinScale = 0.5;
        private const double MaxScale = 2.0;
        private const double ScaleStep = 0.1;

        public double UiScale
        {
            get => _uiScale;
            set
            {
                var rounded = Math.Round(value, 1);
                if (_uiScale != rounded)
                {
                    this.RaiseAndSetIfChanged(ref _uiScale, rounded);
                    SaveUiScale(rounded);
                }
            }
        }

        // --- Diagnostic flag (maps to an mpv-side option; applied live via MainView). The video-quality flags
        // (HighQualityScaling, SharperDeinterlacing) were removed on 2026-10-25: switched back on, they dropped frames. ---

        private bool _logRenderDimensions;
        /// <summary>Lead 2: log the FBO size mpv renders into, to confirm display-native rendering.</summary>
        public bool LogRenderDimensions
        {
            get => _logRenderDimensions;
            set
            {
                if (_logRenderDimensions != value)
                {
                    this.RaiseAndSetIfChanged(ref _logRenderDimensions, value);
                    SaveSetting("LogRenderDimensions", value ? "true" : "false");
                }
            }
        }

        private readonly IConfiguration? _configuration;

        // --- Interface: Classic / New. Changing it saves the setting and restarts Baird into that mode. ---
        private Baird.Services.UiMode _uiMode;
        private bool _switching;
        public Baird.Services.UiMode UiMode { get => _uiMode; private set { this.RaiseAndSetIfChanged(ref _uiMode, value); this.RaisePropertyChanged(nameof(UiModeText)); } }
        public string UiModeText => _uiMode == Baird.Services.UiMode.New ? "New" : "Classic";
        public ReactiveCommand<Unit, Unit> ToggleUiModeCommand { get; }
        /// <summary>Called after the choice is saved; restarts the app (replaceable in tests).</summary>
        public Action Restart { get; set; } = () => Baird.Services.AppRestarter.ForThisProcess().Restart();

        public ReactiveCommand<Unit, Unit> IncreaseScaleCommand { get; }
        public ReactiveCommand<Unit, Unit> DecreaseScaleCommand { get; }
        public ReactiveCommand<Unit, Unit> BackCommand { get; }
        public ReactiveCommand<Unit, Unit> ClearKeyLogCommand { get; }

        public ObservableCollection<string> KeyLogEntries { get; } = new ObservableCollection<string>();

        private bool _hasKeyLogEntries;
        public bool HasKeyLogEntries
        {
            get => _hasKeyLogEntries;
            private set => this.RaiseAndSetIfChanged(ref _hasKeyLogEntries, value);
        }

        public event EventHandler? BackRequested;

        public SettingsViewModel(IConfiguration? configuration = null)
        {
            _configuration = configuration;
            ConfigPath = configuration != null ? Baird.Services.IniSettingWriter.DefaultPath : null;

            // Load persisted settings if available (set backing fields directly to avoid
            // re-persisting during load).
            if (_configuration != null)
            {
                var scaleStr = _configuration["Baird:UiScale"];
                if (double.TryParse(scaleStr, out double savedScale))
                {
                    _uiScale = Math.Clamp(savedScale, MinScale, MaxScale);
                }
                _logRenderDimensions = ParseBool(_configuration["Baird:LogRenderDimensions"]);
            }
            if (_configuration != null) _uiMode = Baird.Services.UiModes.Read(_configuration);
            ToggleUiModeCommand = ReactiveCommand.Create(() =>
            {
                // One switch per run: a held or repeated OK mustn't flip it back while Baird restarts.
                if (_switching) return;
                _switching = true;
                var next = _uiMode == Baird.Services.UiMode.New ? Baird.Services.UiMode.Classic : Baird.Services.UiMode.New;
                SaveSetting(Baird.Services.UiModes.SettingKey, Baird.Services.UiModes.ToSetting(next));
                UiMode = next;
                Restart();
            });

            IncreaseScaleCommand = ReactiveCommand.Create(() =>
            {
                if (UiScale < MaxScale)
                {
                    UiScale += ScaleStep;
                }
            });

            DecreaseScaleCommand = ReactiveCommand.Create(() =>
            {
                if (UiScale > MinScale)
                {
                    UiScale -= ScaleStep;
                }
            });

            BackCommand = ReactiveCommand.Create(() =>
            {
                BackRequested?.Invoke(this, EventArgs.Empty);
            });

            ClearKeyLogCommand = ReactiveCommand.Create(() =>
            {
                KeyLogEntries.Clear();
                HasKeyLogEntries = false;
            });
        }

        public void LogKeyPress(string keyDescription)
        {
            KeyLogEntries.Insert(0, keyDescription);
            while (KeyLogEntries.Count > 50)
                KeyLogEntries.RemoveAt(KeyLogEntries.Count - 1);
            HasKeyLogEntries = true;
        }

        private static bool ParseBool(string? value) => bool.TryParse(value, out var b) && b;

        private void SaveUiScale(double scale)
            => SaveSetting("UiScale", scale.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));

        /// <summary>
        /// Where settings are saved. ~/.baird/config.ini when the app runs (a configuration is given); null when built
        /// without one (tests), so tests never write the real config unless they choose a path.
        /// </summary>
        public string? ConfigPath { get; set; }

        /// <summary>Persists a single key under the [Baird] section of the config file.</summary>
        private void SaveSetting(string key, string value)
        {
            if (ConfigPath == null) return;
            try { Baird.Services.IniSettingWriter.Save(ConfigPath, "Baird", key, value); }
            catch (Exception ex) { Console.WriteLine($"[Settings] Failed to save {key}: {ex.Message}"); }
        }
    }
}
