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
        public static readonly TimeSpan Duration = TimeSpan.FromSeconds(2.2);
        public static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(600);
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

        private readonly Avalonia.Labs.Lottie.Lottie _disc = new(new Uri("avares://Baird/")) { Path = Asset, RepeatCount = 1, AutoPlay = false, Stretch = Stretch.Uniform };
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
            try { _disc.Start(); }
            catch (Exception ex) { Console.WriteLine($"[Intro] The animation couldn't play: {ex.Message}"); }
            _timer = DispatcherTimer.RunOnce(Finish, Duration);
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
