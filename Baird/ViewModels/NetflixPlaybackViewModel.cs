using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Baird.Services;
using ReactiveUI;

namespace Baird.ViewModels
{
    /// <summary>
    /// The page shown when a Netflix title is played from Baird (search, Continue Watching). It runs the proven playback flow
    /// (<see cref="NetflixSpikeViewModel"/>) on the shared Netflix browser and shows its progress and errors. When the video
    /// plays, the flow asks for Baird's HUD; when the HUD's Back stops playback, the page reports <see cref="Finished"/>.
    /// </summary>
    public class NetflixPlaybackViewModel : ReactiveObject
    {
        private readonly NetflixSpikeViewModel _flow;
        private readonly NetflixSession? _session;
        private NetflixHudViewModel? _hud;
        private bool _finished;

        public MediaItemViewModel Item { get; }

        public event Action<NetflixHudViewModel>? HudRequested;
        /// <summary>Last position when the HUD closed, for history and Continue Watching.</summary>
        public event Action<TimeSpan, TimeSpan>? ProgressReported;
        /// <summary>Playback is over (Back, or cancelled); raised once. Not raised for a failure: the page stays and shows it.</summary>
        public event Action? Finished;

        public NetflixPlaybackViewModel(MediaItemViewModel item, NetflixSpikeViewModel flow, NetflixSession? session)
        {
            Item = item;
            _flow = flow;
            _session = session;
            _flow.UseBairdHud = true;
            _flow.HudTitle = item.Name;
            _flow.PropertyChanged += OnFlowChanged;
            _flow.HudRequested += OnHudRequested;
        }

        public string Title => Item.Name;
        /// <summary>A simple text for each step; for a problem, the flow's own message, which says what went wrong.</summary>
        public string StatusText => _flow.State switch
        {
            _ when IsProblem => _flow.StatusMessage,
            NetflixSpikeState.Idle or NetflixSpikeState.LaunchingHiddenBrowser => "Starting Netflix…",
            NetflixSpikeState.CheckingAuth or NetflixSpikeState.SigningIn or NetflixSpikeState.SignedIn => "Signing in to Netflix…",
            NetflixSpikeState.BufferingVideo => "Loading the video…",
            NetflixSpikeState.Playing => "Playing.",
            _ => "Stopping…",
        };
        public bool IsProblem => _flow.IsProblem || _flow.IsMfaRequired;
        public bool IsWorking => !IsProblem && !_flow.IsStopped;
        public string HintText => IsProblem ? "Press Back to return." : "Press Back to cancel.";

        public async Task StartAsync()
        {
            if (_session != null) _session.IsPlaybackActive = true;
            _flow.TargetUrl = Item.StreamUrl ?? "";
            Console.WriteLine($"[NetflixPlayback] Playing \"{Item.Name}\" ({_flow.TargetUrl}).");
            await _flow.LaunchPlaybackFlowAsync();
            Console.WriteLine($"[NetflixPlayback] Flow settled: {_flow.State} \"{_flow.StatusMessage}\"");
            if (!_flow.CanStop) ReleaseBrowser(); // failed or not signed in: the browser is no longer playing
        }

        /// <summary>The page was left (Back before the video started): stop the browser.</summary>
        public async Task CancelAsync()
        {
            if (_finished) return;
            Console.WriteLine("[NetflixPlayback] Page left; stopping Netflix.");
            if (_flow.CanStop) await _flow.StopPlaybackAsync();
            Finish();
        }

        private void OnHudRequested(NetflixHudViewModel hud)
        {
            _hud = hud;
            hud.Closed += () =>
            {
                if (hud.DurationSeconds > 0)
                {
                    ProgressReported?.Invoke(TimeSpan.FromSeconds(hud.PositionSeconds), TimeSpan.FromSeconds(hud.DurationSeconds));
                }
            };
            HudRequested?.Invoke(hud);
        }

        private void OnFlowChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(NetflixSpikeViewModel.StatusMessage)) this.RaisePropertyChanged(nameof(StatusText));
            if (e.PropertyName != nameof(NetflixSpikeViewModel.State)) return;

            this.RaisePropertyChanged(nameof(IsProblem));
            this.RaisePropertyChanged(nameof(IsWorking));
            this.RaisePropertyChanged(nameof(StatusText));
            this.RaisePropertyChanged(nameof(HintText));
            if (_flow.IsStopped && _hud != null) Finish(); // Back on the HUD
        }

        private void ReleaseBrowser()
        {
            if (_session != null) _session.IsPlaybackActive = false;
        }

        private void Finish()
        {
            if (_finished) return;
            _finished = true;
            ReleaseBrowser();
            _flow.PropertyChanged -= OnFlowChanged;
            _flow.HudRequested -= OnHudRequested;
            _flow.Dispose();
            Console.WriteLine("[NetflixPlayback] Finished.");
            Finished?.Invoke();
        }
    }
}
