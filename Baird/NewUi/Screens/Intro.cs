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
            Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = stage });
            Transitions = new Avalonia.Animation.Transitions
            {
                new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = FadeOut, Easing = T.Ease },
            };
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
        public void Skip() => Finish();

        public bool IsFinishing => _finishing;

        private void Finish()
        {
            if (_finishing) return;
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
