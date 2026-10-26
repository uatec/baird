using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Baird.NewUi.Controls;
using Baird.NewUi.Core;
using Baird.NewUi.Model;
using Baird.NewUi.Shell;
using Baird.NewUi.Theme;

namespace Baird.NewUi.Screens
{
    /// <summary>
    /// Home: a hero that describes the lit card (no buttons of its own), then rows — Up next, On now, Watchlist,
    /// Discover. ▴ off the top row returns false so the shell lights the tab bar.
    /// </summary>
    public sealed class HomeScreen : IScreen
    {
        private const double RowTop = 590;
        private static readonly Dictionary<CardShape, double> RowHeight = new() { [CardShape.Wide] = 412, [CardShape.Poster] = 520 };
        private static readonly Dictionary<CardShape, double> Step = new() { [CardShape.Wide] = 384 + 36, [CardShape.Poster] = 216 + 36 };

        private sealed class Row
        {
            public required string Title;
            public required string Eyebrow;
            public required CardShape Shape;
            public List<Card> Cards = new();
            public Control View = null!;
            public StackPanel Track = null!;
        }

        private readonly NewUiContext _ctx;
        private readonly Panel _root = new() { Width = 1920, Height = 1080, Background = Brushes.Transparent };
        private readonly Panel _heroArt = new();
        private readonly Border _scrim;
        private readonly StackPanel _hero = new() { Width = 900, Spacing = 0, Margin = new Thickness(T.SafeX, 160, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        private readonly Canvas _rows = new() { Width = 1920, Height = 1080 };
        private List<Row> _rowList = new();
        private (int Row, int Col) _pos = (0, 0);
        private readonly Dictionary<int, int> _memory = new();
        private bool _lit = true;
        private bool _overVideo;
        private string? _heroKey;

        public Control View => _root;
        public Control? LitElement => _lit && _rowList.Count > 0 && _rowList[_pos.Row].Cards.Count > 0 ? _rowList[_pos.Row].Cards[_pos.Col] : null;

        public HomeScreen(NewUiContext ctx)
        {
            _ctx = ctx;
            _scrim = new Border
            {
                Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative), GradientStops = { new GradientStop(Color.Parse("#F2000000"), 0), new GradientStop(Color.Parse("#B8000000"), 0.38), new GradientStop(Color.Parse("#33000000"), 0.7), new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.85) } },
            };
            var bottom = new Border
            {
                Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative), GradientStops = { new GradientStop(Color.Parse("#EB000000"), 0), new GradientStop(Color.Parse("#59000000"), 0.42), new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.62) } },
            };
            var top = new Border { Height = 300, VerticalAlignment = VerticalAlignment.Top, Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative), GradientStops = { new GradientStop(Color.Parse("#D0000000"), 0), new GradientStop(Color.FromArgb(0, 0, 0, 0), 1) } } };
            _root.Children.Add(_heroArt);
            _root.Children.Add(_scrim);
            _root.Children.Add(bottom);
            _root.Children.Add(top);
            _root.Children.Add(_hero);
            _root.Children.Add(_rows);
            _ = LoadAsync();
            _ctx.UpNextChanged += () => _ = RefreshUpNextAsync();
            // Keep On now current while Home is up (programmes change all day).
            var refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            refresh.Tick += (_, _) => { if (_root.IsEffectivelyVisible && _root.GetVisualRoot() != null) _ = RefreshOnNowAsync(); };
            _root.AttachedToVisualTree += (_, _) => refresh.Start();
            _root.DetachedFromVisualTree += (_, _) => refresh.Stop();
        }

        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Completes once Home's rows are on screen (the start-up animation waits for it).</summary>
        public Task Ready => _ready.Task;

        public async Task LoadAsync()
        {
            try { await LoadRowsAsync(); }
            catch (Exception ex) { Console.WriteLine($"[Home] Loading failed: {ex.Message}"); _ready.TrySetResult(); }
        }

        private async Task LoadRowsAsync()
        {
            var now = _ctx.Now();
            var upNext = _ctx.Home.UpNextAsync();
            var onNow = _ctx.Home.OnNowAsync(now);
            var watch = _ctx.Home.WatchlistAsync();
            var disc = _ctx.Home.DiscoverAsync();
            await Task.WhenAll(upNext, onNow, watch, disc);
            _ctx.Channels = onNow.Result.Where(c => c.Channel != null).Select(c => c.Channel!).ToList();
            Dispatcher.UIThread.Post(() => Build(new (string, string, CardShape, IReadOnlyList<HomeCard>)[]
            {
                ("Up next", "Up next", CardShape.Wide, upNext.Result),
                ("On now", "On now", CardShape.Wide, onNow.Result),
                ("Your watchlist", "In your watchlist", CardShape.Poster, watch.Result),
                ("Discover", "Discover", CardShape.Poster, disc.Result),
            }));
            Dispatcher.UIThread.Post(() => _ready.TrySetResult());
        }

        /// <summary>What's playing goes first in the first row, so one OK from Home goes back to it.</summary>
        private IEnumerable<(string Title, string Eyebrow, CardShape Shape, IReadOnlyList<HomeCard> Cards)> WithNowPlaying(
            IEnumerable<(string Title, string Eyebrow, CardShape Shape, IReadOnlyList<HomeCard> Cards)> rows)
        {
            var list = rows.Select(r => (r.Title, r.Eyebrow, r.Shape, (IReadOnlyList<HomeCard>)r.Cards.Where(c => c.Badge != "playing").ToList())).ToList();
            if (_ctx.NowPlaying is not { } now) return list;
            if (list.Count == 0 || list[0].Shape != CardShape.Wide) list.Insert(0, ("Up next", "Up next", CardShape.Wide, Array.Empty<HomeCard>()));
            var first = list[0];
            list[0] = (first.Title, first.Item2, first.Shape, first.Item4.Where(c => c.Id != now.Id).Prepend(now).ToList());
            return list;
        }

        public void Build(IEnumerable<(string Title, string Eyebrow, CardShape Shape, IReadOnlyList<HomeCard> Cards)> rows)
        {
            rows = WithNowPlaying(rows);
            _rowList = rows.Where(r => r.Cards.Count > 0).Select(r =>
            {
                var row = new Row { Title = r.Title, Eyebrow = r.Eyebrow, Shape = r.Shape };
                row.Cards = r.Cards.Select(c => new Card(c, r.Shape)).ToList();
                row.Track = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 36, Margin = new Thickness(T.SafeX, 0, 0, 0) };
                foreach (var c in row.Cards) row.Track.Children.Add(c);
                Motion.Animate(row.Track, Motion.Med);
                var title = new TextBlock { Text = r.Title, FontSize = 30, FontWeight = FontWeight.SemiBold, Foreground = T.Text2, Margin = new Thickness(T.SafeX, 0, 0, 30) };
                row.View = new StackPanel { Children = { title, row.Track } };
                Motion.Animate(row.View, Motion.Med);
                return row;
            }).ToList();
            _rows.Children.Clear();
            foreach (var r in _rowList) _rows.Children.Add(r.View);
            _pos = (Math.Min(_pos.Row, Math.Max(0, _rowList.Count - 1)), 0);
            Layout();
        }

        private void Layout()
        {
            if (_rowList.Count == 0) { RenderHero(); return; }
            // Taller poster rows sit higher, so a lit poster (lift, ring, label) is fully on screen.
            var y = _rowList[_pos.Row].Shape == CardShape.Poster ? RowTop - 90 : RowTop;
            for (var i = 0; i < _rowList.Count; i++)
            {
                var r = _rowList[i];
                var above = i < _pos.Row;
                r.View.Opacity = above ? 0 : 1;
                r.View.RenderTransform = TransformOperations.Parse($"translate(0px, {(above ? RowTop - 200 : y)}px)");
                if (!above) y += RowHeight[r.Shape];
                var col = i == _pos.Row ? _pos.Col : (_memory.TryGetValue(i, out var m) ? m : 0);
                r.Track.RenderTransform = TransformOperations.Parse($"translate({-Math.Max(0, col - 1) * Step[r.Shape]}px, 0px)");
                for (var j = 0; j < r.Cards.Count; j++)
                {
                    var lit = _lit && i == _pos.Row && j == _pos.Col;
                    r.Cards[j].Lit = lit;
                    r.Cards[j].Dimmed = _lit && i == _pos.Row && !lit;
                }
                ((TextBlock)((StackPanel)r.View).Children[0]).Foreground = _lit && i == _pos.Row ? T.Text : T.Text2;
            }
            RenderHero();
        }

        private void RenderHero()
        {
            if (_overVideo && _ctx.Playback.Current is { } now)
            {
                if (now.Kind == "live")
                {
                    var prog = _ctx.Programme;
                    SetHero("playing:" + now.ItemId + prog?.Id, null, $"● Live · Now playing on {now.Channel?.Name}", prog?.Title ?? now.Title,
                        prog != null ? $"{prog.Start.LocalDateTime:HH:mm}–{prog.End.LocalDateTime:HH:mm}{MinLeft(prog)}" : null, prog?.Synopsis);
                }
                else
                {
                    var pb = _ctx.Playback;
                    var ends = pb.Duration > TimeSpan.Zero ? $"Ends at {DateTime.Now + (pb.Duration - pb.Position):HH:mm}" : null;
                    SetHero("playing:" + now.ItemId, null, "Now playing", now.Title, string.Join("  ·  ", new[] { _ctx.NowPlaying?.Sublabel, ends }.Where(x => !string.IsNullOrEmpty(x))), _ctx.NowPlaying?.Synopsis);
                }
                return;
            }
            if (_rowList.Count == 0) { SetHero("empty", null, "Welcome", "Baird", "Loading what's on and what you're watching…", null); return; }
            var row = _rowList[_pos.Row];
            var c = row.Cards[_pos.Col].Data;
            var eyebrow = c.Kind == CardKind.Channel ? $"● Live   {c.Label}" : $"{row.Eyebrow}{(c.Source is { Length: > 0 } s && s != "Seerr" && s != "TV" ? $" · {s.Split(':')[0]}" : "")}";
            var title = c.Kind == CardKind.Channel ? c.Programme?.Title ?? c.Label : c.Label;
            var meta = c.Kind == CardKind.Channel ? (c.Programme is { } p ? $"{p.Start.LocalDateTime:HH:mm}–{p.End.LocalDateTime:HH:mm}{MinLeft(p)}" : "No programme information") : c.Sublabel;
            SetHero(c.Id, c.Kind == CardKind.Channel ? c.Programme?.ImageUrl : c.ImageUrl, eyebrow, title, meta, c.Synopsis);
        }

        private string MinLeft(Programme p)
        {
            var left = (int)Math.Ceiling((p.End - _ctx.Now()).TotalMinutes);
            return left > 0 && left < 600 ? $"  ·  {left} min left" : "";
        }

        private void SetHero(string key, string? image, string eyebrow, string title, string? meta, string? synopsis)
        {
            _hero.Children.Clear();
            _hero.Children.Add(new TextBlock { Text = eyebrow, FontSize = 26, FontWeight = FontWeight.SemiBold, Foreground = eyebrow.StartsWith("●") ? T.Live : T.Text2, Margin = new Thickness(0, 0, 0, 18) });
            _hero.Children.Add(new TextBlock { Text = title, FontSize = title.Length > 22 ? 84 : T.Hero, FontWeight = FontWeight.Bold, Foreground = T.Text, TextWrapping = TextWrapping.Wrap, LineHeight = (title.Length > 22 ? 84 : T.Hero) * 0.98, Margin = new Thickness(0, 0, 0, 22) });
            if (!string.IsNullOrEmpty(meta)) _hero.Children.Add(new TextBlock { Text = meta, FontSize = T.Body, FontWeight = FontWeight.SemiBold, Foreground = T.Text, Margin = new Thickness(0, 0, 0, 16) });
            if (!string.IsNullOrEmpty(synopsis)) _hero.Children.Add(new TextBlock { Text = synopsis, FontSize = T.Body, Foreground = T.Text2, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.WordEllipsis, MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left });

            if (key == _heroKey) return;
            _heroKey = key;
            // Crossfade the backdrop: the new one fades in over the old, which is removed once it's covered.
            foreach (var old in _heroArt.Children.ToList()) { Motion.Animate(old, T.Slow); old.Opacity = 0; DispatcherTimer.RunOnce(() => _heroArt.Children.Remove(old), T.Slow + TimeSpan.FromMilliseconds(50)); }
            if (_overVideo || string.IsNullOrEmpty(image)) return;
            // Shown whole if it's a portrait poster (on the right, over a dimmed fill), never cropped through a face.
            var img = new Baird.NewUi.Controls.ArtImage(image) { OpacityMask = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative), GradientStops = { new GradientStop(Colors.Black, 0.4), new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.92) } } };
            img.Shown += poster => img.Main.Margin = poster ? new Thickness(0, 0, 96, 0) : default;
            _heroArt.Children.Add(img);
        }

        public bool OnAction(UiAction a, bool repeat)
        {
            if (_rowList.Count == 0) return a != UiAction.Up && a != UiAction.Back;
            switch (a)
            {
                case UiAction.Up when _pos.Row == 0: return false;
                case UiAction.PageUp or UiAction.PageDown:
                    // Four cards along the row at a time.
                    var row = _rowList[_pos.Row];
                    _pos = (_pos.Row, Math.Clamp(_pos.Col + (a == UiAction.PageDown ? 4 : -4), 0, row.Cards.Count - 1));
                    _overVideo = false;
                    Layout();
                    return true;
                case UiAction.Up or UiAction.Down or UiAction.Left or UiAction.Right:
                    var dir = a switch { UiAction.Up => Dir.Up, UiAction.Down => Dir.Down, UiAction.Left => Dir.Left, _ => Dir.Right };
                    _pos = FocusMath.MoveInRows(_rowList.Select(r => r.Cards.Count).ToList(), _pos, dir, _memory);
                    _overVideo = false;
                    Layout();
                    return true;
                case UiAction.Ok or UiAction.PlayPause:
                    _ctx.Activate(_rowList[_pos.Row].Cards[_pos.Col].Data);
                    return true;
                case UiAction.HoldOk:
                    _ctx.Nav.OpenOverlay(new OverlayRef(_rowList[_pos.Row].Title == "Up next" ? "itemactions-upnext" : "itemactions", _rowList[_pos.Row].Cards[_pos.Col].Data));
                    return true;
            }
            return false;
        }

        public void OnShow()
        {
            _overVideo = _ctx.Nav.State.Video != null;
            _heroKey = null;
            // Put what's playing (or take what stopped) at the front of the first row, lit when something plays.
            if (_rowList.Count > 0)
            {
                Build(_rowList.Select(r => (r.Title, r.Eyebrow, r.Shape, (IReadOnlyList<HomeCard>)r.Cards.Select(c => c.Data).ToList())).ToList());
                if (_ctx.NowPlaying != null) { _pos = (0, 0); _memory.Clear(); }
            }
            _ = RefreshUpNextAsync(); _ = RefreshOnNowAsync();
            Layout();
        }
        public void OnFocusIn() { _lit = true; Layout(); }
        public void OnFocusOut() { _lit = false; Layout(); }

        /// <summary>The Home key: back to the first card of Up next.</summary>
        public void Reset() { _pos = (0, 0); _memory.Clear(); _lit = true; Layout(); }

        /// <summary>What's on changes through the day: refresh On now in place (the lit card stays lit).</summary>
        public async Task RefreshOnNowAsync()
        {
            IReadOnlyList<HomeCard> cards;
            try { cards = await _ctx.Home.OnNowAsync(_ctx.Now()); }
            catch (Exception ex) { Console.WriteLine($"[Home] On now refresh failed: {ex.Message}"); return; }
            if (cards.Count == 0) return;
            void Apply()
            {
                var i = _rowList.FindIndex(r => r.Title == "On now");
                if (i < 0) return;
                var same = cards.Select(c => (c.Id, c.Programme?.Id, c.Programme?.ImageUrl)).SequenceEqual(_rowList[i].Cards.Select(c => (c.Data.Id, c.Data.Programme?.Id, c.Data.Programme?.ImageUrl)));
                if (same) return;
                _ctx.Channels = cards.Where(c => c.Channel != null).Select(c => c.Channel!).ToList();
                var pos = _pos;
                var rows = _rowList.Select(r => (r.Title, r.Eyebrow, r.Shape, (IReadOnlyList<HomeCard>)r.Cards.Select(c => c.Data).ToList())).ToList();
                rows[i] = (rows[i].Title, rows[i].Eyebrow, rows[i].Shape, cards);
                Build(rows);
                _pos = (Math.Min(pos.Row, _rowList.Count - 1), Math.Min(pos.Col, Math.Max(0, _rowList[Math.Min(pos.Row, _rowList.Count - 1)].Cards.Count - 1)));
                _heroKey = null;
                Layout();
            }
            if (Dispatcher.UIThread.CheckAccess()) Apply(); else Dispatcher.UIThread.Post(Apply);
        }

        private async Task RefreshUpNextAsync()
        {
            if (_rowList.Count == 0 || _rowList[0].Title != "Up next") return;
            var cards = await _ctx.Home.UpNextAsync();
            Dispatcher.UIThread.Post(() =>
            {
                if (_rowList.Count == 0) return;
                var same = cards.Select(c => c.Id).SequenceEqual(_rowList[0].Cards.Select(c => c.Data.Id));
                if (same) return;
                var rows = _rowList.Select(r => (r.Title, r.Eyebrow, r.Shape, (IReadOnlyList<HomeCard>)r.Cards.Select(c => c.Data).ToList())).ToList();
                rows[0] = (rows[0].Title, rows[0].Eyebrow, rows[0].Shape, cards);
                Build(rows);
            });
        }
    }
}
