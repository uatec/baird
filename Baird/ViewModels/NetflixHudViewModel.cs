using System;
using System.Threading.Tasks;
using Avalonia.Input;
using Baird.Services;
using ReactiveUI;

namespace Baird.ViewModels
{
    /// <summary>
    /// Baird's own HUD over Netflix. Baird keeps keyboard focus: the remote's keys come here and become commands to Netflix's
    /// player over CDP. The browser shows only the video, below Baird's transparent HUD window.
    /// </summary>
    public class NetflixHudViewModel : ReactiveObject
    {
        public const double SeekStepSeconds = 10;

        private readonly ICdpBrowserService _browser;
        private DateTime _lastShown;
        private bool _closed;

        public event Action? ExitRequested;
        /// <summary>Raised once when playback ends for any reason, so the HUD window can close.</summary>
        public event Action? Closed;

        public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;
        public TimeSpan HudTimeout { get; set; } = TimeSpan.FromSeconds(5);
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

        public NetflixHudViewModel(ICdpBrowserService browser)
        {
            _browser = browser;
            _lastShown = DateTime.MinValue;
        }

        private double _position;
        public double PositionSeconds
        {
            get => _position;
            private set
            {
                this.RaiseAndSetIfChanged(ref _position, value);
                this.RaisePropertyChanged(nameof(PositionText));
                this.RaisePropertyChanged(nameof(Progress));
            }
        }

        private double _duration;
        public double DurationSeconds
        {
            get => _duration;
            private set
            {
                this.RaiseAndSetIfChanged(ref _duration, value);
                this.RaisePropertyChanged(nameof(DurationText));
                this.RaisePropertyChanged(nameof(Progress));
            }
        }

        private bool _isPaused;
        public bool IsPaused
        {
            get => _isPaused;
            private set
            {
                this.RaiseAndSetIfChanged(ref _isPaused, value);
                this.RaisePropertyChanged(nameof(PlayStateText));
            }
        }

        private bool _isHudVisible = true;
        public bool IsHudVisible
        {
            get => _isHudVisible;
            private set => this.RaiseAndSetIfChanged(ref _isHudVisible, value);
        }

        private string _statusText = "";
        public string StatusText
        {
            get => _statusText;
            private set => this.RaiseAndSetIfChanged(ref _statusText, value);
        }

        public string Title => "Netflix";
        public string PositionText => Format(PositionSeconds);
        public string DurationText => Format(DurationSeconds);
        public double Progress => DurationSeconds > 0 ? Math.Clamp(PositionSeconds / DurationSeconds, 0, 1) : 0;
        public string PlayStateText => IsPaused ? "❚❚  Paused" : "▶  Playing";

        public async Task PollAsync()
        {
            if (_closed) return;
            if (_lastShown == DateTime.MinValue) _lastShown = Now(); // show the HUD when playback starts
            var status = await _browser.PollVideoPlaybackAsync();
            if (status == null)
            {
                Problem("No contact with the browser. Press Back to return to Baird.");
            }
            else if (!status.Found)
            {
                Problem("No video on the Netflix page. Press Back to return to Baird.");
            }
            else
            {
                StatusText = "";
                PositionSeconds = status.CurrentTime;
                DurationSeconds = status.Duration;
                IsPaused = status.Paused;
            }

            IsHudVisible = IsPaused || StatusText.Length > 0 || Now() - _lastShown < HudTimeout;
        }

        /// <summary>Handles a remote key. Returns false for keys the HUD does not use; every key still shows the HUD.</summary>
        public async Task<bool> HandleKeyAsync(Key key)
        {
            ShowHud();
            switch (key)
            {
                case Key.Left:
                    await SeekByAsync(-SeekStepSeconds);
                    return true;
                case Key.Right:
                    await SeekByAsync(SeekStepSeconds);
                    return true;
                case Key.Enter:
                case Key.Space:
                case Key.MediaPlayPause:
                    await TogglePauseAsync();
                    return true;
                case Key.Escape:
                case Key.BrowserBack:
                case Key.Back:
                    Console.WriteLine($"[NetflixHud] Back ({key}): exit requested.");
                    ExitRequested?.Invoke();
                    return true;
                default:
                    return false;
            }
        }

        public async Task SeekByAsync(double seconds)
        {
            var path = await _browser.ControlNetflixPlayerAsync(NetflixPlayerAction.SeekBy, seconds);
            Console.WriteLine($"[NetflixHud] Seek {seconds:+0;-0}s from {PositionText} via {path ?? "nothing"}.");
            if (path == null)
            {
                Problem("The Netflix player did not accept the seek.");
                return;
            }
            // Show the new position at once; the next poll corrects it.
            var max = DurationSeconds > 0 ? DurationSeconds : double.MaxValue;
            PositionSeconds = Math.Clamp(PositionSeconds + seconds, 0, max);
        }

        public async Task TogglePauseAsync()
        {
            var action = IsPaused ? NetflixPlayerAction.Play : NetflixPlayerAction.Pause;
            var path = await _browser.ControlNetflixPlayerAsync(action);
            Console.WriteLine($"[NetflixHud] {action} at {PositionText} via {path ?? "nothing"}.");
            if (path == null)
            {
                Problem($"The Netflix player did not accept {action}.");
                return;
            }
            IsPaused = action == NetflixPlayerAction.Pause;
        }

        public void Close()
        {
            if (_closed) return;
            _closed = true;
            Closed?.Invoke();
        }

        private void ShowHud()
        {
            _lastShown = Now();
            IsHudVisible = true;
        }

        private void Problem(string message)
        {
            if (StatusText != message) Console.WriteLine($"[NetflixHud] {message}");
            StatusText = message;
            IsHudVisible = true;
        }

        private static string Format(double seconds)
        {
            if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
        }
    }
}
