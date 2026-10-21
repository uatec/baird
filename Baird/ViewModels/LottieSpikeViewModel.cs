using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Reactive;

namespace Baird.ViewModels
{
    public class LottieAnimationInfo
    {
        public string Title { get; }
        public string Description { get; }
        public string Path { get; }
        public string Category { get; }
        public string TargetFeature { get; }

        public LottieAnimationInfo(string title, string description, string path, string category, string targetFeature)
        {
            Title = title;
            Description = description;
            Path = path;
            Category = category;
            TargetFeature = targetFeature;
        }

        public override string ToString() => Title;
    }

    public class LottieSpikeViewModel : ReactiveObject
    {
        public event EventHandler? BackRequested;

        // View action requests
        public event Action? RequestPlay;
        public event Action? RequestPause;
        public event Action? RequestRestart;
        public event Action<float>? RequestSeek;

        private LottieAnimationInfo _selectedAnimation;
        public LottieAnimationInfo SelectedAnimation
        {
            get => _selectedAnimation;
            set
            {
                if (_selectedAnimation != value && value != null)
                {
                    this.RaiseAndSetIfChanged(ref _selectedAnimation, value);
                    this.RaisePropertyChanged(nameof(CurrentAnimationPath));
                    ResetRepetitionCount();
                    UpdateStatus();
                }
            }
        }

        public string CurrentAnimationPath => SelectedAnimation?.Path ?? string.Empty;

        public ObservableCollection<LottieAnimationInfo> Animations { get; }

        private bool _isPlaying = true;
        public bool IsPlaying
        {
            get => _isPlaying;
            set
            {
                this.RaiseAndSetIfChanged(ref _isPlaying, value);
                this.RaisePropertyChanged(nameof(PlayPauseButtonText));
                UpdateStatus();
            }
        }

        public string PlayPauseButtonText => IsPlaying ? "Pause ⏸" : "Play ▶";

        private int _playbackRate = 1;
        public int PlaybackRate
        {
            get => _playbackRate;
            set
            {
                if (value < 1) value = 1;
                if (value > 5) value = 5;
                this.RaiseAndSetIfChanged(ref _playbackRate, value);
                this.RaisePropertyChanged(nameof(PlaybackRateText));
                UpdateStatus();
            }
        }

        public string PlaybackRateText => $"{PlaybackRate}x";

        private int _repeatCount = -1; // -1 = Infinite
        public int RepeatCount
        {
            get => _repeatCount;
            set
            {
                this.RaiseAndSetIfChanged(ref _repeatCount, value);
                this.RaisePropertyChanged(nameof(IsLooping));
                this.RaisePropertyChanged(nameof(LoopModeText));
                UpdateStatus();
            }
        }

        public bool IsLooping
        {
            get => _repeatCount == -1;
            set => RepeatCount = value ? -1 : 1;
        }

        public string LoopModeText => IsLooping ? "Loop: Infinite 🔁" : "Loop: Once 🔂";

        private bool _autoPlay = true;
        public bool AutoPlay
        {
            get => _autoPlay;
            set => this.RaiseAndSetIfChanged(ref _autoPlay, value);
        }

        private int _repetitionCount;
        public int RepetitionCount
        {
            get => _repetitionCount;
            set
            {
                this.RaiseAndSetIfChanged(ref _repetitionCount, value);
                UpdateStatus();
            }
        }

        private double _seekProgress;
        public double SeekProgress
        {
            get => _seekProgress;
            set => this.RaiseAndSetIfChanged(ref _seekProgress, Math.Clamp(value, 0.0, 1.0));
        }

        public string ProgressPercentText => $"{(int)(SeekProgress * 100)}%";

        private string _statusText = "Ready";
        public string StatusText
        {
            get => _statusText;
            private set => this.RaiseAndSetIfChanged(ref _statusText, value);
        }

        // Commands
        public ReactiveCommand<Unit, Unit> BackCommand { get; }
        public ReactiveCommand<Unit, Unit> TogglePlayPauseCommand { get; }
        public ReactiveCommand<Unit, Unit> PlayCommand { get; }
        public ReactiveCommand<Unit, Unit> PauseCommand { get; }
        public ReactiveCommand<Unit, Unit> RestartCommand { get; }
        public ReactiveCommand<int, Unit> SetSpeedCommand { get; }
        public ReactiveCommand<Unit, Unit> ToggleLoopCommand { get; }
        public ReactiveCommand<LottieAnimationInfo, Unit> SelectAnimationCommand { get; }
        public ReactiveCommand<Unit, Unit> NextAnimationCommand { get; }
        public ReactiveCommand<Unit, Unit> PreviousAnimationCommand { get; }
        public ReactiveCommand<double, Unit> SeekCommand { get; }

        public LottieSpikeViewModel()
        {
            Animations = new ObservableCollection<LottieAnimationInfo>
            {
                new LottieAnimationInfo(
                    title: "Loading Ring",
                    description: "Smooth vector loading spinner for buffering, channel tuning, and background sync.",
                    path: "avares://Baird/Assets/Lottie/loading-ring.json",
                    category: "Buffering & Tuner Feedback",
                    targetFeature: "EPG tuning spinner, Jellyfin media buffering indicator"
                ),
                new LottieAnimationInfo(
                    title: "Audio Visualizer Bars",
                    description: "Dynamic dancing equalizer bars with gradient vector fill for radio / music playback.",
                    path: "avares://Baird/Assets/Lottie/audio-bars.json",
                    category: "Audio / Now Playing",
                    targetFeature: "Live radio stream feedback, music track visualizer HUD"
                ),
                new LottieAnimationInfo(
                    title: "Play / Pause Toggle",
                    description: "Morphing vector icon that transitions fluidly between play and pause states.",
                    path: "avares://Baird/Assets/Lottie/play-pause.json",
                    category: "Transport Controls",
                    targetFeature: "OSD transport controls, CEC remote feedback popups"
                ),
                new LottieAnimationInfo(
                    title: "Wave Pulse Loader",
                    description: "Geometric undulating wave loader for voice recognition and search indexing.",
                    path: "avares://Baird/Assets/Lottie/wave-loader.json",
                    category: "Speech / Search Status",
                    targetFeature: "Whisper.net listening pulse, OmniSearch indexing"
                )
            };

            _selectedAnimation = Animations[0];

            BackCommand = ReactiveCommand.Create(() => BackRequested?.Invoke(this, EventArgs.Empty));

            TogglePlayPauseCommand = ReactiveCommand.Create(() =>
            {
                if (IsPlaying)
                {
                    Pause();
                }
                else
                {
                    Play();
                }
            });

            PlayCommand = ReactiveCommand.Create(Play);
            PauseCommand = ReactiveCommand.Create(Pause);
            RestartCommand = ReactiveCommand.Create(Restart);
            SetSpeedCommand = ReactiveCommand.Create<int>(speed => PlaybackRate = speed);
            ToggleLoopCommand = ReactiveCommand.Create(() => { IsLooping = !IsLooping; });
            SelectAnimationCommand = ReactiveCommand.Create<LottieAnimationInfo>(item =>
            {
                if (item != null)
                {
                    SelectedAnimation = item;
                }
            });

            NextAnimationCommand = ReactiveCommand.Create(() =>
            {
                var index = Animations.IndexOf(SelectedAnimation);
                if (index < Animations.Count - 1)
                {
                    SelectedAnimation = Animations[index + 1];
                }
                else
                {
                    SelectedAnimation = Animations[0];
                }
            });

            PreviousAnimationCommand = ReactiveCommand.Create(() =>
            {
                var index = Animations.IndexOf(SelectedAnimation);
                if (index > 0)
                {
                    SelectedAnimation = Animations[index - 1];
                }
                else
                {
                    SelectedAnimation = Animations[Animations.Count - 1];
                }
            });

            SeekCommand = ReactiveCommand.Create<double>(progress =>
            {
                SeekProgress = progress;
                RequestSeek?.Invoke((float)progress);
            });

            UpdateStatus();
        }

        public void Play()
        {
            IsPlaying = true;
            RequestPlay?.Invoke();
            UpdateStatus();
        }

        public void Pause()
        {
            IsPlaying = false;
            RequestPause?.Invoke();
            UpdateStatus();
        }

        public void Restart()
        {
            _repetitionCount = 0;
            IsPlaying = true;
            RequestRestart?.Invoke();
            UpdateStatus();
        }

        public void OnRepetitionCompleted(int repetition)
        {
            RepetitionCount = repetition;
        }

        public void OnAnimationFinished()
        {
            if (!IsLooping)
            {
                IsPlaying = false;
                UpdateStatus();
            }
        }

        public void ResetRepetitionCount()
        {
            RepetitionCount = 0;
        }

        private void UpdateStatus()
        {
            var state = IsPlaying ? $"Playing ({PlaybackRate}x)" : "Paused";
            var loopInfo = IsLooping ? $"Infinite Loop (Loops: {RepetitionCount})" : $"Single Run (Loops: {RepetitionCount})";
            StatusText = $"{state} • {loopInfo} • SkiaSharp.Skottie Native Vector";
        }
    }
}
