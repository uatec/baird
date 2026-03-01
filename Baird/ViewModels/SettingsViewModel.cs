using ReactiveUI;
using System;
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

        private readonly IConfiguration? _configuration;

        public ReactiveCommand<Unit, Unit> IncreaseScaleCommand { get; }
        public ReactiveCommand<Unit, Unit> DecreaseScaleCommand { get; }
        public ReactiveCommand<Unit, Unit> BackCommand { get; }

        public event EventHandler? BackRequested;

        public SettingsViewModel(IConfiguration? configuration = null)
        {
            _configuration = configuration;

            // Load persisted UI Scale if available
            if (_configuration != null)
            {
                var scaleStr = _configuration["Baird:UiScale"];
                if (double.TryParse(scaleStr, out double savedScale))
                {
                    _uiScale = Math.Clamp(savedScale, MinScale, MaxScale);
                }
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
        }

        private void SaveUiScale(double scale)
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

                // Find and replace or add Baird:UiScale
                bool settingsSectionFound = false;
                bool scaleFound = false;

                for (int i = 0; i < lines.Count; i++)
                {
                    var line = lines[i].Trim();
                    if (line.Equals("[Baird]", StringComparison.OrdinalIgnoreCase))
                    {
                        settingsSectionFound = true;
                    }
                    else if (settingsSectionFound && line.StartsWith("UiScale", StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = $"UiScale={scale.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}";
                        scaleFound = true;
                        break;
                    }
                    else if (settingsSectionFound && line.StartsWith("["))
                    {
                        // Start of new section before finding UiScale, insert here
                        lines.Insert(i, $"UiScale={scale.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}");
                        scaleFound = true;
                        break;
                    }
                }

                if (!settingsSectionFound)
                {
                    lines.Add("[Baird]");
                    lines.Add($"UiScale={scale.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}");
                }
                else if (!scaleFound)
                {
                    lines.Add($"UiScale={scale.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}");
                }

                File.WriteAllLines(configPath, lines);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SettingsViewModel] Failed to save UiScale: {ex.Message}");
            }
        }
    }
}
