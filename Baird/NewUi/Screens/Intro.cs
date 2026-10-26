using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Baird.NewUi.Shell;
using Baird.NewUi.Theme;

namespace Baird.NewUi.Screens
{
    /// <summary>
    /// The start-up animation. Its first frame is the boot splash exactly (the wordmark centred on black; the Pi shows the
    /// same picture from power-on), so boot → Baird is seamless. A Nipkow disc (Baird's mechanical-TV scanning disc, a
    /// Lottie animation) turns in around the wordmark, then the whole layer fades to reveal Home underneath.
    /// </summary>
    public sealed class Intro : Panel
    {
        public static readonly TimeSpan Duration = TimeSpan.FromSeconds(2.5);   // the animation's length
        /// <summary>If the animation never reports it's done (slow to load, or can't play), the intro ends anyway.</summary>
        public static readonly TimeSpan Fallback = TimeSpan.FromSeconds(4.5);
        public static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(600);
        /// <summary>After the window's first frames, a moment for the compositor to put it on screen.</summary>
        public static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(300);
        public const string Asset = "avares://Baird/Assets/Lottie/baird-intro.json";

        public TextBlock Wordmark { get; } = new()
        {
            Text = "Baird",
            FontSize = 150,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White,
            FontFamily = T.Font,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        private readonly Avalonia.Labs.Lottie.Lottie _disc = new(new Uri("avares://Baird/")) { RepeatCount = 1, AutoPlay = true, Stretch = Stretch.Uniform };
        private Action? _done;
        private IDisposable? _timer;
        private bool _finishing;

        public Intro()
        {
            Background = Brushes.Black;
            var stage = new Panel { Width = 1920, Height = 1080 };
            stage.Children.Add(_disc);
            stage.Children.Add(Wordmark);
            stage.Children.Add(_spinner);
            Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = stage });
            Transitions = new Avalonia.Animation.Transitions
            {
                new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = FadeOut, Easing = T.Ease },
            };
        }

        /// <summary>How long it will wait for Home before showing it anyway.</summary>
        public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(25);
        private System.Threading.Tasks.Task? _ready;
        private readonly Avalonia.Controls.Shapes.Arc _spinner = new()
        {
            Width = 40,
            Height = 40,
            StartAngle = 0,
            SweepAngle = 100,
            Stroke = Brushes.White,
            StrokeThickness = 4,
            Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 330, 0, 0),
        };
        private DispatcherTimer? _spin;

        /// <summary>The animation has played but Home isn't ready: the spinner shows and the intro stays.</summary>
        public bool Waiting { get; private set; }

        /// <summary>Plays, then stays (with a spinner) until <paramref name="ready"/> completes, so Home appears ready.</summary>
        public void Play(Action done, System.Threading.Tasks.Task? ready)
        {
            _ready = ready;
            Play(done);
        }

        public void Play(Action done)
        {
            _done = done;
            // Start once the window has actually drawn (the Pi is busy at start-up): the clock must not run while
            // nothing is on screen, or the disc is missed.
            var top = TopLevel.GetTopLevel(this);
            void AfterFirstFrames() => top!.RequestAnimationFrame(_ => top.RequestAnimationFrame(_ => DispatcherTimer.RunOnce(Begin, SettleDelay)));
            if (top is Window { IsVisible: false } w)
            {
                // Not on screen yet (the compositor maps the window a moment after it opens): wait for it.
                void Opened(object? s, EventArgs e) { w.Opened -= Opened; AfterFirstFrames(); }
                w.Opened += Opened;
            }
            else if (top != null) AfterFirstFrames();
            else Begin();
        }

        private void Begin()
        {
            if (_finishing) return;
            // Load it now it's on screen: it starts itself once loaded (the Pi is slow at start-up) and the intro ends
            // when it does; the splash frame (identical to the boot picture) holds until then.
            _disc.AnimationCompleted += (_, _) => Dispatcher.UIThread.Post(Finish);
            try { _disc.Path = Asset; }
            catch (Exception ex) { Console.WriteLine($"[Intro] The animation couldn't play: {ex.Message}"); }
            _timer = DispatcherTimer.RunOnce(Finish, Fallback);
        }

        /// <summary>A key during the intro: go straight to Home.</summary>
        public void Skip() { _ready = null; Finish(); }

        public bool IsFinishing => _finishing;

        private void Finish()
        {
            if (_finishing) return;
            if (_ready is { IsCompleted: false } ready && !Waiting)
            {
                // The animation's done but Home isn't: hold the picture, with a spinner, until it is.
                Waiting = true;
                _timer?.Dispose();
                Motion.Animate(_spinner, T.Fast);
                _spinner.Opacity = 1;
                _spin = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
                _spin.Tick += (_, _) => _spinner.StartAngle = (_spinner.StartAngle + 12) % 360;
                if (!Motion.Lite) _spin.Start();
                _ = System.Threading.Tasks.Task.WhenAny(ready, System.Threading.Tasks.Task.Delay(MaxWait))
                    .ContinueWith(_ => Dispatcher.UIThread.Post(() => { _ready = null; _spin?.Stop(); Finish(); }));
                return;
            }
            _finishing = true;
            _timer?.Dispose();
            Opacity = 0;
            DispatcherTimer.RunOnce(() =>
            {
                IsVisible = false;
                try { _disc.Stop(); } catch { /* already stopped */ }
                _done?.Invoke();
            }, FadeOut);
        }
    }
}
