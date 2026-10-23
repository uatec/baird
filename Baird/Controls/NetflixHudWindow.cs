using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Baird.ViewModels;

namespace Baird.Controls
{
    /// <summary>
    /// Fullscreen transparent window over the Netflix browser. It keeps keyboard focus, so the remote drives Baird, and
    /// draws only a bottom bar; everywhere else the video below shows through. Same window settings as the Wayland
    /// overlay spike (spikes/wayland-overlay), which kept focus and 0 dropped frames on the Pi with a 1 Hz HUD.
    /// The HUD repaints only when a value changes (about 2 Hz while shown, nothing while hidden).
    /// </summary>
    public class NetflixHudWindow : Window
    {
        private readonly NetflixHudViewModel _vm;
        private readonly DispatcherTimer _pollTimer;
        private bool _polling;
        private readonly TextBlock _title = new(), _state = new(), _position = new(), _duration = new(), _status = new();
        private readonly ProgressBar _progress = new();
        private Border _bar = new();

        public NetflixHudWindow(NetflixHudViewModel vm)
        {
            _vm = vm;
            DataContext = vm;
            Title = "Baird Netflix HUD";
            SystemDecorations = SystemDecorations.None;
            WindowState = WindowState.FullScreen;
            Topmost = true;
            ShowInTaskbar = false;
            Background = Brushes.Transparent;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            Cursor = new Cursor(StandardCursorType.None);
            Content = BuildHud();
            // Set values directly, not with bindings: the shipped build is trimmed and code bindings use reflection.
            Update();
            vm.PropertyChanged += (_, _) => Update();

            _pollTimer = new DispatcherTimer { Interval = vm.PollInterval };
            _pollTimer.Tick += async (_, _) =>
            {
                if (_polling) return;
                _polling = true;
                try { await _vm.PollAsync(); }
                catch (Exception ex) { Console.WriteLine($"[NetflixHud] Poll failed: {ex.Message}"); }
                finally { _polling = false; }
            };

            Opened += (_, _) =>
            {
                Console.WriteLine($"[NetflixHud] Window opened: transparency={ActualTransparencyLevel} state={WindowState} topmost={Topmost} size={ClientSize.Width}x{ClientSize.Height}");
                _pollTimer.Start();
                Activate();
            };
            Activated += (_, _) => Console.WriteLine("[NetflixHud] Focus: HUD window activated (Baird has the keys).");
            Deactivated += (_, _) => Console.WriteLine("[NetflixHud] Focus: HUD window DEACTIVATED (another window took the keys).");
            Closed += (_, _) => _pollTimer.Stop();
            vm.Closed += () => Dispatcher.UIThread.Post(Close);
        }

        protected override async void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            Console.WriteLine($"[NetflixHud] Key: {e.Key}, physical {e.PhysicalKey}");
            e.Handled = true; // Baird owns every key while Netflix plays
            try { await _vm.HandleKeyAsync(e.Key); }
            catch (Exception ex) { Console.WriteLine($"[NetflixHud] Key {e.Key} failed: {ex.Message}"); }
        }

        private void Update()
        {
            _title.Text = _vm.Title;
            _state.Text = _vm.PlayStateText;
            _position.Text = _vm.PositionText;
            _duration.Text = _vm.DurationText;
            _progress.Value = _vm.Progress;
            _status.Text = _vm.StatusText;
            _status.IsVisible = _vm.StatusText.Length > 0;
            _bar.IsVisible = _vm.IsHudVisible;
        }

        private Control BuildHud()
        {
            void Style(TextBlock t, double size, FontWeight weight = FontWeight.Normal, IBrush? brush = null)
            {
                t.FontSize = size;
                t.FontWeight = weight;
                t.Foreground = brush ?? Brushes.White;
                t.VerticalAlignment = VerticalAlignment.Center;
            }

            _progress.Minimum = 0;
            _progress.Maximum = 1;
            _progress.Height = 8;
            _progress.MinHeight = 8;
            _progress.CornerRadius = new CornerRadius(4);
            _progress.Foreground = new SolidColorBrush(Color.Parse("#E50914"));
            _progress.Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

            var times = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            Style(_duration, 26, brush: new SolidColorBrush(Color.Parse("#CBD5E1")));
            DockPanel.SetDock(_duration, Dock.Right);
            times.Children.Add(_duration);
            Style(_position, 26, FontWeight.SemiBold);
            times.Children.Add(_position);

            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
            Style(_state, 28, FontWeight.SemiBold);
            DockPanel.SetDock(_state, Dock.Right);
            top.Children.Add(_state);
            Style(_title, 40, FontWeight.Bold);
            top.Children.Add(_title);

            Style(_status, 26, FontWeight.SemiBold, new SolidColorBrush(Color.Parse("#FCA5A5")));
            _status.Margin = new Thickness(0, 12, 0, 0);
            _status.TextWrapping = TextWrapping.Wrap;

            var hints = new TextBlock
            {
                Text = "◄ ►  Seek 10 s      OK  Play / Pause      BACK  Return to Baird",
                FontSize = 22,
                Foreground = new SolidColorBrush(Color.Parse("#94A3B8")),
                Margin = new Thickness(0, 16, 0, 0),
            };

            _bar = new Border
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                Padding = new Thickness(72, 64, 72, 48),
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0),
                        new GradientStop(Color.FromArgb(0xB0, 0, 0, 0), 0.35),
                        new GradientStop(Color.FromArgb(0xE0, 0, 0, 0), 1),
                    },
                },
                Child = new StackPanel { Children = { top, _progress, times, _status, hints } },
            };

            return new Grid { Background = Brushes.Transparent, Children = { _bar } };
        }
    }
}
