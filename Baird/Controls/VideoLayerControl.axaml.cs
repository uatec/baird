using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.Configuration;
using System.Linq;
using Avalonia;

namespace Baird.Controls
{
    public partial class VideoLayerControl : UserControl
    {
        public event EventHandler? ExitRequested;

        public VideoLayerControl()
        {
            InitializeComponent();
        }

        public Baird.Services.IDataService? DataService
        {
            get => GetPlayer()?.DataService;
            set
            {
                var player = GetPlayer();
                if (player != null) player.DataService = value;
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        // Expose Player for external control if needed, or binding
        public VideoPlayer? GetPlayer()
        {
            return this.FindControl<VideoPlayer>("Player");
        }

        protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            var player = GetPlayer();
            if (player != null)
            {
                player.SearchRequested += OnSearchRequested;
                player.UserActivity += OnUserActivity;
                player.StreamEnded += OnStreamEnded;
                player.ConfigurationToggleRequested += OnConfigurationToggleRequested;
                player.ExitRequested += OnExitRequested;
                player.EpgRequested += OnEpgRequested;
                player.Focus(); // Ensure player gets focus when layer is active
            }
        }

        protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            var player = GetPlayer();
            if (player != null)
            {
                player.SearchRequested -= OnSearchRequested;
                player.UserActivity -= OnUserActivity;
                player.StreamEnded -= OnStreamEnded;
                player.ConfigurationToggleRequested -= OnConfigurationToggleRequested;
                player.ExitRequested -= OnExitRequested;
                player.EpgRequested -= OnEpgRequested;
            }
        }

        private void OnEpgRequested(object? sender, EventArgs e)
        {
            if (DataContext is ViewModels.MainViewModel vm)
            {
                vm.ToggleEpgOverlay();
            }
        }

        private void OnUserActivity(object? sender, EventArgs e)
        {
            if (DataContext is ViewModels.MainViewModel vm)
            {
                vm.ResetHudTimer();
            }
        }

        private void OnSearchRequested(object? sender, string text)
        {
            if (DataContext is ViewModels.MainViewModel vm)
            {
                vm.OmniSearch.Clear();
                // Open MainMenu focused on Search tab
                var searchIndex = vm.MainMenu.Tabs.ToList().FindIndex(t => t.Content == vm.OmniSearch);
                vm.OpenMainMenu(tabIndex: searchIndex >= 0 ? searchIndex : 0);

                if (!string.IsNullOrEmpty(text))
                {
                    vm.OmniSearch.SearchText = text;
                    // Don't select-all when entering via a digit key — the digit is already
                    // in the box and the caret should sit at the end so further digits append.
                    vm.OmniSearch.RequestSearchBoxFocus(selectAll: false);
                }
            }
        }

        private void OnStreamEnded(object? sender, EventArgs e)
        {
            Console.WriteLine("[VideoLayerControl] StreamEnded event received, checking for next episode");

            // Ensure we're on the UI thread before calling navigation
            // TODO; why on UI thread?
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                if (DataContext is ViewModels.MainViewModel vm)
                {
                    await vm.PlayNextEpisodeOrGoBack();
                }
            });
        }
        public void ToggleConfiguration() => OnConfigurationToggleRequested(this, EventArgs.Empty);

        private void OnConfigurationToggleRequested(object? sender, EventArgs e)
        {
            var configLayer = this.FindControl<Border>("ConfigLayer");
            if (configLayer == null) return;

            configLayer.IsVisible = !configLayer.IsVisible;

            if (configLayer.IsVisible)
            {
                var app = (App)Application.Current!;
                var config = app.Configuration;

                // Only show keys that come from the config.ini files: the environment holds
                // unrelated (possibly sensitive) variables that shouldn't appear on the TV.
                var fileProviders = (config as IConfigurationRoot)?.Providers
                    .Where(p => p is not Microsoft.Extensions.Configuration.EnvironmentVariables.EnvironmentVariablesConfigurationProvider)
                    .ToList();

                var items = config.AsEnumerable()
                    .Where(x => fileProviders == null || fileProviders.Any(p => p.TryGet(x.Key, out _)))
                    .OrderBy(x => x.Key)
                    .Select(x => new KeyValuePair<string, string?>(x.Key, MaskConfigValue(x.Key, x.Value)))
                    .ToList();

                var itemsControl = this.FindControl<ItemsControl>("ConfigItemsControl");
                if (itemsControl != null)
                {
                    itemsControl.ItemsSource = items;
                }
            }
        }

        private static readonly System.Text.RegularExpressions.Regex SecretKeyPattern =
            new("pass|key|token|secret|auth", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static string? MaskConfigValue(string key, string? value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            if (SecretKeyPattern.IsMatch(key)) return "***";
            return Baird.Services.SecretRedactor.Redact(value);
        }

        private void OnExitRequested(object? sender, EventArgs e)
        {
            Console.WriteLine("[VideoLayerControl] Exit requested, propagating to MainView");
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
