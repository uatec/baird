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

        // --- Video-quality feature flags (each maps to an mpv option; applied live via MainView) ---

        private bool _highQualityScaling;
        /// <summary>Lead 1: spline36 scaler instead of default bilinear.</summary>
        public bool HighQualityScaling
        {
            get => _highQualityScaling;
            set
            {
                if (_highQualityScaling != value)
                {
                    this.RaiseAndSetIfChanged(ref _highQualityScaling, value);
                    SaveSetting("HighQualityScaling", value ? "true" : "false");
                }
            }
        }

        private bool _sharperDeinterlacing;
        /// <summary>Lead 3: bwdif instead of default yadif for live deinterlacing.</summary>
        public bool SharperDeinterlacing
        {
            get => _sharperDeinterlacing;
            set
            {
                if (_sharperDeinterlacing != value)
                {
                    this.RaiseAndSetIfChanged(ref _sharperDeinterlacing, value);
                    SaveSetting("SharperDeinterlacing", value ? "true" : "false");
                }
            }
        }

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

            // Load persisted settings if available (set backing fields directly to avoid
            // re-persisting during load).
            if (_configuration != null)
            {
                var scaleStr = _configuration["Baird:UiScale"];
                if (double.TryParse(scaleStr, out double savedScale))
                {
                    _uiScale = Math.Clamp(savedScale, MinScale, MaxScale);
                }
                _highQualityScaling = ParseBool(_configuration["Baird:HighQualityScaling"]);
                _sharperDeinterlacing = ParseBool(_configuration["Baird:SharperDeinterlacing"]);
                _logRenderDimensions = ParseBool(_configuration["Baird:LogRenderDimensions"]);
            }
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
        /// Persists a single key under the [Baird] section of ~/.baird/config.ini, updating it in
        /// place if present, inserting it into the section, or creating the section as needed. Other
        /// settings and surrounding lines are preserved.
        /// </summary>
        private void SaveSetting(string key, string value)
        {
            try
            {
                var userProfile = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
                var configDir = Path.Combine(userProfile, ".baird");
                var configPath = Path.Combine(configDir, "config.ini");

                if (!Directory.Exists(configDir))
                {
                    Directory.CreateDirectory(configDir);
                }

                // Read all lines to preserve other settings
                var lines = File.Exists(configPath) ? File.ReadAllLines(configPath).ToList() : new System.Collections.Generic.List<string>();

                bool settingsSectionFound = false;
                bool keyFound = false;

                for (int i = 0; i < lines.Count; i++)
                {
                    var line = lines[i].Trim();
                    if (line.Equals("[Baird]", StringComparison.OrdinalIgnoreCase))
                    {
                        settingsSectionFound = true;
                    }
                    else if (settingsSectionFound && line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = $"{key}={value}";
                        keyFound = true;
                        break;
                    }
                    else if (settingsSectionFound && line.StartsWith("["))
                    {
                        // Start of a new section before finding the key — insert it here.
                        lines.Insert(i, $"{key}={value}");
                        keyFound = true;
                        break;
                    }
                }

                if (!settingsSectionFound)
                {
                    lines.Add("[Baird]");
                    lines.Add($"{key}={value}");
                }
                else if (!keyFound)
                {
                    lines.Add($"{key}={value}");
                }

                File.WriteAllLines(configPath, lines);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SettingsViewModel] Failed to save {key}: {ex.Message}");
            }
        }
    }
}
