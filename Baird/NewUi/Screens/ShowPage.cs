using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Baird.NewUi.Controls;
using Baird.NewUi.Core;
using Baird.NewUi.Model;
using Baird.NewUi.Shell;
using Baird.NewUi.Theme;

namespace Baird.NewUi.Screens
{
    /// <summary>
    /// One page per show or film, wherever it streams (prototype: screens/show.js): backdrop, title, where it's available,
    /// synopsis; buttons (Resume / Next / Play, Request…, Watchlist); for a series, season tabs (moving across them
    /// switches the episodes straight away; each says its state, never a call to action) and the episodes with progress,
    /// watched marks and download percentages. Requests go through one Request… sheet; a missing episode offers its season.
    /// </summary>
    public sealed class ShowPage : IScreen
    {
        private const double EpStep = 384 + 36;
        private const double LowerTop = 640;
        /// <summary>When the lit episode is this close to the end of a season that has more pages, the next page is fetched.</summary>
        private const int LoadAhead = 8;
        private static readonly TimeSpan Poll = TimeSpan.FromSeconds(30);

        private sealed record ButtonDef(string Label, string? Icon, bool Primary, Action Run);
        private enum RowKind { Buttons, Seasons, Episodes }

        private readonly NewUiContext _ctx;
        private CatalogueTitle _title;
        private ShowDetails? _details;
        private readonly Panel _root = new() { Width = 1920, Height = 1080, Background = Brushes.Black };
        private readonly Panel _art = new();
        private readonly Panel _content = new();
        private readonly StackPanel _body = new() { Width = 1000, Spacing = 0, Margin = new Thickness(T.SafeX, 96, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        private readonly StackPanel _buttonsRow = new() { Orientation = Orientation.Horizontal, Spacing = 18, Margin = new Thickness(0, 30, 0, 0), Height = 76 };
        private readonly StackPanel _lower = new() { Spacing = 24, Margin = new Thickness(0, LowerTop, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = TabSpacing, Margin = new Thickness(T.SafeX, 0, 0, 0), Height = 72, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly StackPanel _epTrack = new() { Orientation = Orientation.Horizontal, Spacing = 36, Margin = new Thickness(T.SafeX, 0, 0, 0) };
        private List<ButtonDef> _buttons = new();
        private List<PillButton> _buttonEls = new();
        private List<SeasonTab> _tabEls = new();
        private List<Card> _epEls = new();
        private List<RowKind> _rows = new() { RowKind.Buttons };
        private (int Row, int Col) _pos;
        private readonly Dictionary<int, int> _memory = new();
        private int _season; // index into Seasons
        private readonly CancellationTokenSource _life = new();
        private IDisposable? _poll;
        private bool _moved;
        private bool _loadingMore;
        private (Channel Channel, Programme Programme)? _onTv;
        private bool Checking => _details?.CheckingLibrary == true && _title.Sources.Count == 0;

        public Control View => _root;
        public Task Loaded { get; private set; }
        /// <summary>The page of episodes being fetched as the viewer nears the end of a long season (completed when none is).</summary>
        public Task LoadingMore { get; private set; } = Task.CompletedTask;
        public ShowDetails? Details => _details;
        public IReadOnlyList<string> ButtonLabels => _buttons.Select(b => b.Label).ToList();
        public int SeasonIndex => _season;
        public Control? LitElement => _rows[_pos.Row] switch
        {
            RowKind.Buttons => _buttonEls.ElementAtOrDefault(_pos.Col),
            RowKind.Seasons => _tabEls.ElementAtOrDefault(_pos.Col),
            _ => _epEls.ElementAtOrDefault(_pos.Col),
        };

        public ShowPage(NewUiContext ctx, CatalogueTitle title)
        {
            _ctx = ctx; _title = title;
            _root.Children.Add(_art);
            _root.Children.Add(new Border { Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative), GradientStops = { new GradientStop(Color.Parse("#F5000000"), 0), new GradientStop(Color.Parse("#C0000000"), 0.45), new GradientStop(Color.Parse("#40000000"), 0.8) } } });
            _root.Children.Add(new Border { Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative), GradientStops = { new GradientStop(Color.Parse("#F0000000"), 0), new GradientStop(Color.Parse("#80000000"), 0.4), new GradientStop(Colors.Transparent, 0.7) } } });
            _content.Children.Add(_body);
            _content.Children.Add(_lower);
            _lower.Children.Add(_tabs);
            _lower.Children.Add(new Panel { Children = { _epTrack } });
            Motion.Animate(_content, Motion.Med);
            Motion.Animate(_epTrack, Motion.Med);
            Motion.Animate(_tabs, Motion.Med);
            _root.Children.Add(_content);
            SetArt(title.ImageUrl);
            RenderBody();
            Loaded = LoadAsync();
            OnTvLoaded = LoadOnTvAsync();
        }

        /// <summary>When the title next airs (from the guide), for the "On TV …" button.</summary>
        public Task OnTvLoaded { get; }

        private async Task LoadOnTvAsync()
        {
            if (_ctx.Search == null || _title.Kind == TitleKind.Episode) return;
            try
            {
                var air = await _ctx.Search.NextShowingAsync(_title.Name, _ctx.Channels, _ctx.Now(), _life.Token);
                if (air == null) return;
                OnUi(() => { _onTv = air; if (_details != null) Rebuild(); });
            }
            catch (OperationCanceledException) { }
        }

        private void SetArt(string? url)
        {
            _art.Children.Clear();
            if (string.IsNullOrEmpty(url)) return;
            var img = new Image { Stretch = Stretch.UniformToFill, Opacity = 0, OpacityMask = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative), GradientStops = { new GradientStop(Colors.Black, 0.3), new GradientStop(Colors.Transparent, 0.95) } } };
            _art.Children.Add(img);
            // Prefetched while its card was lit: there at once, no fade.
            if (ImageCache.TryGet(url, out var cached) && cached != null) { img.Source = cached; img.Opacity = 0.75; Motion.Animate(img, T.Slow); return; }
            Motion.Animate(img, T.Slow);
            img.PropertyChanged += (_, e) => { if (e.Property == Image.SourceProperty && img.Source != null) img.Opacity = 0.75; };
            AsyncImageLoader.ImageLoader.SetSource(img, url);
        }

        // ------------------------------------------------------------------ data
        private async Task LoadAsync(bool fresh = false)
        {
            if (_ctx.Shows == null) return;
            try
            {
                // What Seerr knows shows at once; each library source that has it fills in as it answers.
                var d = await _ctx.Shows.DetailsAsync(_title, partial => OnUi(() => Apply(partial)), _life.Token, fresh);
                OnUi(() => Apply(d));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[ShowPage] {_title.Name} failed to load: {ex.Message}");
                OnUi(() => { _details = new ShowDetails(_title, Array.Empty<Season>()); Rebuild(); });
            }
        }

        private static void OnUi(Action a) { if (Dispatcher.UIThread.CheckAccess()) a(); else Dispatcher.UIThread.Post(a); }

        private void Apply(ShowDetails d)
        {
            if (_life.IsCancellationRequested || ReferenceEquals(d, _details)) return;
            // Choose where to open until the viewer moves (more of the show may arrive as the library answers).
            var first = !_moved;
            if (d.Title.ImageUrl != _title.ImageUrl) SetArt(d.Title.ImageUrl);
            _details = d; _title = d.Title;
            if (first && d.Seasons.Count > 0)
            {
                // Open on the season of the next episode, else the first season with something to watch.
                var next = d.Next?.Episode;
                var i = next != null ? d.Seasons.ToList().FindIndex(s => s.Number == next.Season) : d.Seasons.ToList().FindIndex(s => s.State is SeasonState.Available or SeasonState.Partial);
                _season = Math.Max(0, i);
                _memory[1] = _season;
                if (next != null) _memory[2] = Math.Max(0, d.Seasons[_season].Episodes.ToList().IndexOf(next));
            }
            Rebuild();
            _poll?.Dispose();
            var downloading = d.Seasons.Any(s => s.State == SeasonState.Requested || s.Episodes.Any(e => e.State == EpisodeState.Requested)) || d.FilmState == SeasonState.Requested;
            if (downloading) _poll = _ctx.Timers.After(Poll, () => { Loaded = LoadAsync(fresh: true); });
        }

        // ------------------------------------------------------------------ building
        private void Rebuild()
        {
            _buttons = Buttons();
            _rows = new() { RowKind.Buttons };
            if (_details is { Seasons.Count: > 0 }) { _rows.Add(RowKind.Seasons); _rows.Add(RowKind.Episodes); }
            _season = Math.Clamp(_season, 0, Math.Max(0, (_details?.Seasons.Count ?? 1) - 1));
            _pos = (Math.Min(_pos.Row, _rows.Count - 1), _pos.Col);
            RenderBody();
            RenderLower();
            _pos = (_pos.Row, Math.Min(_pos.Col, Math.Max(0, RowCount(_pos.Row) - 1)));
            Draw();
        }

        private int RowCount(int row) => _rows[row] switch { RowKind.Buttons => _buttonEls.Count, RowKind.Seasons => _tabEls.Count, _ => _epEls.Count };

        private void RenderBody()
        {
            var t = _title;
            _body.Children.Clear();
            _body.Children.Add(new TextBlock { Text = t.Name, FontSize = t.Name.Length > 18 ? 80 : 100, FontWeight = FontWeight.Bold, Foreground = T.Text, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 16) });
            var seasons = _details?.Seasons.Count(s => s.Number > 0) ?? 0;
            var meta = string.Join("   ", new[]
            {
                t.Year?.ToString(),
                t.Kind == TitleKind.Series ? seasons > 0 ? $"Series · {seasons} season{(seasons == 1 ? "" : "s")}" : "Series"
                    : t.Kind == TitleKind.Episode ? "Episode" : t.Best?.Duration is { TotalMinutes: > 0 } dur ? $"Film · {(int)dur.TotalHours} h {dur.Minutes} min" : "Film",
            }.Where(x => x != null));
            _body.Children.Add(new TextBlock { Text = meta, FontSize = T.Body, FontWeight = FontWeight.SemiBold, Foreground = T.Text2 });
            var where = _details?.Summary ?? (t.Sources.Count == 0 && t.Kind != TitleKind.Episode ? ShowDetails.Checking : t.Where);
            _body.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 14,
                Margin = new Thickness(0, 14, 0, 0),
                Children = { new Icon(Icon.Series, 26, T.Text2) { VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = where, FontSize = T.Body, FontWeight = FontWeight.SemiBold, Foreground = T.Text, TextWrapping = TextWrapping.Wrap, MaxWidth = 940 } },
            });
            if (t.Synopsis is { Length: > 0 } syn)
                _body.Children.Add(new TextBlock { Text = syn, FontSize = 27, Foreground = T.Text2, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.WordEllipsis, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) });

            _buttonEls = _details == null
                ? new() { new PillButton("Loading…", null, false) }
                : _buttons.Select(b => new PillButton(b.Label, b.Icon, b.Primary)).ToList();
            _buttonsRow.Children.Clear();
            foreach (var b in _buttonEls) _buttonsRow.Children.Add(b);
            _body.Children.Add(_buttonsRow);
        }

        private void RenderLower()
        {
            _tabEls = new(); _epEls = new();
            _lower.IsVisible = _details is { Seasons.Count: > 0 };
            if (_details is not { Seasons.Count: > 0 } d) return;
            _tabs.Children.Clear();
            _tabEls = d.Seasons.Select(s => new SeasonTab(s, Checking)).ToList();
            foreach (var tab in _tabEls) _tabs.Children.Add(tab);
            RenderEpisodes();
        }

        private void RenderEpisodes()
        {
            _epTrack.Children.Clear();
            var s = _details!.Seasons[_season];
            _epEls = s.Episodes.Select(e => new Card(EpisodeCard(e), CardShape.Wide)).ToList();
            foreach (var c in _epEls) _epTrack.Children.Add(c);
            if (_epEls.Count == 0)
                _epTrack.Children.Add(new TextBlock { Text = "No episodes listed for this season.", FontSize = T.Body, Foreground = T.Text2 });
        }

        /// <summary>Plays an episode, telling the player the show's order so the end card can offer the next one.</summary>
        private void PlayEpisode(Episode e)
        {
            if (_details != null)
            {
                var playable = _details.Seasons.SelectMany(s => s.Episodes).Where(x => x.State == EpisodeState.Available && x.Source != null).ToList();
                _ctx.Following = playable.Select(EpisodeCard).ToList();
                _ctx.FollowingShow = _title.Name;
                _ctx.FollowingInfo = playable.Zip(_ctx.Following).GroupBy(x => x.Second.Id).ToDictionary(g => g.Key, g => (EpisodeTitle(g.First().First), EpisodeMeta(g.First().First)));
            }
            _ctx.Activate(EpisodeCard(e));
        }

        /// <summary>"The Variant" (the label without its number).</summary>
        private static string EpisodeTitle(Episode e) => e.Label.StartsWith($"{e.Number}. ", StringComparison.Ordinal) ? e.Label[$"{e.Number}. ".Length..] : e.Label;

        /// <summary>"Season 1, episode 2 · 45 min".</summary>
        private static string EpisodeMeta(Episode e) =>
            $"Season {e.Season}, episode {e.Number}{(e.Source?.Duration is { TotalMinutes: >= 1 } d ? $" · {d.TotalMinutes:0} min" : "")}";

        private HomeCard EpisodeCard(Episode e)
        {
            var next = _details?.Next?.Episode;
            var sub = string.Join("  ·  ", new[] { e.Sublabel, e.Watched ? "Watched" : e == next ? "Up next" : null }.Where(x => x != null));
            var badge = e.State switch
            {
                EpisodeState.Requested => e.Download is { } d ? $"dl:{d * 100:0}" : "requested",
                EpisodeState.Missing => Checking ? null : "missing",
                _ => e.Watched ? "watched" : null,
            };
            var src = e.Source;
            return new HomeCard(CardKind.Episode, src?.ItemId ?? $"{_title.Key}:{e.Code}", e.Label, sub.Length > 0 ? sub : null, e.ImageUrl,
                e.Watched ? 1 : src?.Progress, badge, src?.Service ?? "", src?.Via ?? PlayVia.None, src?.StreamUrl, src?.Resume ?? TimeSpan.Zero, Synopsis: e.Synopsis);
        }

        private HomeCard FilmCard(TitleSource b) =>
            new(CardKind.Film, b.ItemId, _title.Name, null, _title.ImageUrl, b.Progress, null, b.Service, b.Via, b.StreamUrl, b.Resume, Synopsis: _title.Synopsis);

        private List<ButtonDef> Buttons()
        {
            var list = new List<ButtonDef>();
            var d = _details;
            if (d == null) return list;
            var t = d.Title;
            var canRequest = _ctx.Shows?.CanRequest == true && t.Seerr != null;
            if (Checking)
            {
                // Never "not in your library" (nor Request) while a library source is still answering.
                list.Add(new(ShowDetails.Checking, null, true, () => _ctx.Toast($"Still looking for {t.Name} in your library…")));
                return list;
            }
            if (t.Kind == TitleKind.Series && d.Seasons.Count > 0)
            {
                if (d.Next is { } n) list.Add(new(n.Label, Icon.Play, true, () => PlayEpisode(n.Episode)));
                var downloading = d.Seasons.Where(s => s.State == SeasonState.Requested).ToList();
                if (list.Count == 0 && downloading.Count > 0)
                {
                    var pct = downloading.Select(s => s.Download).FirstOrDefault(x => x != null);
                    list.Add(new($"Requested{(pct is { } p ? $" · {p * 100:0}%" : "")}", Icon.Get, true,
                        () => _ctx.Toast($"{ShowDetails.Label(downloading.Select(s => s.Number))} of {t.Name} {(downloading.Count == 1 ? "is" : "are")} on the way. New episodes appear in Up next when they're ready.")));
                }
                if (canRequest && d.MissingSeasons.Any()) list.Add(new("Request…", Icon.Get, list.Count == 0, () => OpenRequest(null, null)));
            }
            else if (t.Best is { } b)
                list.Add(new(b.Resume > TimeSpan.Zero ? "Resume" : "Play", Icon.Play, true, () => _ctx.Activate(FilmCard(b))));
            else if (d.FilmState == SeasonState.Requested)
                list.Add(new($"Requested{(d.FilmDownload is { } p ? $" · {p * 100:0}%" : "")}", Icon.Get, true,
                    () => _ctx.Toast(d.FilmDownload is { } p2 ? $"{t.Name} is downloading ({p2 * 100:0}%). It appears in Up next when it's ready." : $"{t.Name} is requested. It appears in Up next when it's ready.")));
            else if (d.FilmState == SeasonState.Available)
                list.Add(new("In your library", Icon.Check, true, () => _ctx.Toast($"Seerr says {t.Name} is in your library, but Baird can't find it to play yet.")));
            else if (canRequest)
                list.Add(new("Request…", Icon.Get, true, () => OpenRequest(null, null)));

            if (_onTv is { } air)
                list.Add(new($"On TV {Adapters.CatalogueSearch.When(air.Programme, _ctx.Now())} · {air.Channel.Name}", Icon.Series, false, () => _ctx.OpenGuide(air.Channel.Id, air.Programme.Start)));
            if (t.Best != null && _ctx.Shows != null)
            {
                var on = _ctx.Shows.InWatchlist(t);
                list.Add(new(on ? "In watchlist" : "Watchlist", on ? Icon.Check : Icon.Plus, false, ToggleWatchlist));
            }
            if (list.Count == 0) list.Add(new("Not available", null, false, () => _ctx.Toast($"{t.Name} isn't streaming anywhere Baird knows, and Seerr doesn't know it.")));
            return list;
        }

        private async void ToggleWatchlist()
        {
            try
            {
                var on = await _ctx.Shows!.ToggleWatchlistAsync(_title);
                OnUi(() => { _ctx.Toast(on ? $"Added {_title.Name} to your watchlist" : $"Removed {_title.Name} from your watchlist"); Rebuild(); });
            }
            catch (Exception ex) { Console.WriteLine($"[ShowPage] Watchlist failed: {ex.Message}"); }
        }

        // ------------------------------------------------------------------ requests
        /// <summary>The Request… sheet, scoped to a season (and an episode) when opened from one.</summary>
        public void OpenRequest(int? season, int? episode)
        {
            var d = _details;
            if (d == null || _ctx.Shows == null) return;
            var t = d.Title;
            var choices = new List<Choice>();
            if (t.Kind != TitleKind.Series)
                choices.Add(new($"Request {t.Name}", "Downloads to Jellyfin", () => Request(null)));
            else
            {
                var missing = d.MissingSeasons.ToList();
                var s = season is { } n ? d.Seasons.FirstOrDefault(x => x.Number == n) : null;
                if (s is { State: SeasonState.Partial } && episode != null)
                {
                    _ctx.Toast($"Season {s.Number} is partly in your library. Seerr can't request the rest of a season it already has part of.");
                    return;
                }
                if (s is { State: SeasonState.Missing })
                    choices.Add(episode is { } e
                        ? new($"Season {s.Number}, for episode {e}", $"Seerr requests whole seasons: all {s.EpisodeCount} episodes", () => Request(new[] { s.Number }))
                        : new($"Season {s.Number}", $"{s.EpisodeCount} episodes", () => Request(new[] { s.Number })));
                if (missing.Count > 1) choices.Add(new("All missing seasons", ShowDetails.Label(missing), () => Request(missing)));
                foreach (var m in missing.Where(m => m != s?.Number))
                    choices.Add(new($"Season {m}", $"{d.Seasons.First(x => x.Number == m).EpisodeCount} episodes", () => Request(new[] { m })));
            }
            if (choices.Count == 0) { _ctx.Toast("Nothing left to request: every season is in the library or on its way."); return; }
            _ctx.Nav.OpenOverlay(new OverlayRef("choices", new ChoiceList($"Request {t.Name}", "Seerr downloads it to Jellyfin, then it appears in Up next", choices)));
        }

        private async void Request(IReadOnlyList<int>? seasons)
        {
            if (_details == null || _ctx.Shows == null) return;
            try
            {
                var (ok, message) = await _ctx.Shows.RequestAsync(_details, seasons, _life.Token);
                OnUi(() => _ctx.Toast(message));
                if (ok) { Loaded = LoadAsync(fresh: true); await Loaded; }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.WriteLine($"[ShowPage] Request failed: {ex.Message}"); }
        }

        // ------------------------------------------------------------------ drawing
        private double _tabScroll;
        private const double TabSpacing = 12;
        /// <summary>How far the season strip has slid left (tests).</summary>
        public double TabScroll => _tabScroll;

        /// <summary>
        /// Many seasons (The Simpsons has 33) run past the edge: slide the strip just enough that the lit tab (else the
        /// current season's) is wholly on screen, like a row of cards. Moves a transform only; nothing is rebuilt.
        /// </summary>
        private void ScrollTabs()
        {
            if (_tabEls.Count == 0) { _tabScroll = 0; _tabs.RenderTransform = Motion.None; return; }
            var widths = _tabEls.Select(t => { if (t.DesiredSize.Width <= 0) t.Measure(Size.Infinity); return t.DesiredSize.Width; }).ToList();
            var focus = _rows[_pos.Row] == RowKind.Seasons ? _pos.Col : _season;
            _tabScroll = StripScroll(widths, TabSpacing, focus, _tabScroll, 1920 - 2 * T.SafeX, 20);
            _tabs.RenderTransform = TransformOperations.Parse($"translate({-_tabScroll}px, 0px)");
        }

        /// <summary>
        /// The scroll that keeps item <paramref name="index"/> (with <paramref name="pad"/> for its lift) inside a view
        /// <paramref name="view"/> wide, moving as little as possible from <paramref name="current"/>; never past either end.
        /// </summary>
        public static double StripScroll(IReadOnlyList<double> widths, double spacing, int index, double current, double view, double pad)
        {
            var x = widths.Take(index).Sum() + spacing * index;
            var w = widths[index];
            var total = widths.Sum() + spacing * Math.Max(0, widths.Count - 1);
            var scroll = current;
            if (x - pad < scroll) scroll = x - pad;
            if (x + w + pad > scroll + view) scroll = x + w + pad - view;
            return Math.Clamp(scroll, 0, Math.Max(0, total - view + pad));
        }

        private void Draw()
        {
            for (var i = 0; i < _buttonEls.Count; i++) _buttonEls[i].Lit = _rows[_pos.Row] == RowKind.Buttons && i == _pos.Col && _details != null;
            for (var i = 0; i < _tabEls.Count; i++) { _tabEls[i].Lit = _rows[_pos.Row] == RowKind.Seasons && i == _pos.Col; _tabEls[i].Current = i == _season; }
            ScrollTabs();
            var onEps = _rows[_pos.Row] == RowKind.Episodes;
            for (var i = 0; i < _epEls.Count; i++) { _epEls[i].Lit = onEps && i == _pos.Col; _epEls[i].Dimmed = onEps && i != _pos.Col; }
            var col = onEps ? _pos.Col : _memory.TryGetValue(2, out var m) ? m : 0;
            _epTrack.RenderTransform = TransformOperations.Parse($"translate({-Math.Max(0, col - 1) * EpStep}px, 0px)");
            // The lit item is always wholly on screen: the episodes lift the page up.
            _content.RenderTransform = TransformOperations.Parse($"translate(0px, {(onEps ? -150 : 0)}px)");
            if (_rows[_pos.Row] == RowKind.Seasons && _details != null) _ctx.Prefetch?.Season(_details, _season); // the seasons either side
            if (onEps) LoadMore();
        }

        /// <summary>Near the end of a season that lists its episodes a page at a time (a YouTube channel), fetches the next page and adds it on.</summary>
        private void LoadMore()
        {
            if (_loadingMore || _details == null || _ctx.Shows == null) return;
            var season = _details.Seasons[_season];
            if (season.MoreId == null || _pos.Col < season.Episodes.Count - LoadAhead) return;
            _loadingMore = true;
            LoadingMore = LoadMoreAsync(season);
        }

        private async Task LoadMoreAsync(Season season)
        {
            try
            {
                var (episodes, more) = await _ctx.Shows!.MoreEpisodesAsync(season, _life.Token);
                OnUi(() =>
                {
                    var i = _details?.Seasons.ToList().FindIndex(s => s.Number == season.Number && s.MoreId == season.MoreId) ?? -1;
                    if (i < 0 || _life.IsCancellationRequested) return; // the page was refreshed meanwhile: it asks again if it needs to
                    var cur = _details!.Seasons[i];
                    var all = cur.Episodes.Concat(episodes).ToList();
                    var seasons = _details.Seasons.ToList();
                    seasons[i] = cur with { Episodes = all, InLibrary = all.Count, EpisodeCount = Math.Max(all.Count, cur.EpisodeCount ?? 0), MoreId = more };
                    _details = _details with { Seasons = seasons };
                    if (i != _season) return;
                    foreach (var e in episodes)
                    {
                        var card = new Card(EpisodeCard(e), CardShape.Wide);
                        _epEls.Add(card);
                        _epTrack.Children.Add(card);
                    }
                    Draw();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.WriteLine($"[ShowPage] More of {_title.Name} failed: {ex.Message}"); }
            finally { OnUi(() => _loadingMore = false); }
        }

        public bool OnAction(UiAction a, bool repeat)
        {
            if (_details == null) return a is not (UiAction.Back or UiAction.Up);
            switch (a)
            {
                case UiAction.Up when _pos.Row == 0: return false;
                case UiAction.Up or UiAction.Down or UiAction.Left or UiAction.Right:
                    _moved = true;
                    var before = _pos.Row;
                    var dir = a switch { UiAction.Up => Dir.Up, UiAction.Down => Dir.Down, UiAction.Left => Dir.Left, _ => Dir.Right };
                    _pos = FocusMath.MoveInRows(_rows.Select((_, i) => Math.Max(1, RowCount(i))).ToList(), _pos, dir, _memory);
                    // Season tabs are tabs: lighting one shows its episodes.
                    if (_rows[_pos.Row] == RowKind.Seasons && _pos.Col != _season && (a is UiAction.Left or UiAction.Right || before != _pos.Row))
                    {
                        _season = _pos.Col;
                        _memory[2] = 0; // a new season starts at its first episode
                        RenderEpisodes();
                    }
                    if (_rows[_pos.Row] == RowKind.Episodes && _epEls.Count == 0) _pos = (_pos.Row - 1, _season);
                    Draw();
                    return true;
                case UiAction.Ok or UiAction.PlayPause:
                    switch (_rows[_pos.Row])
                    {
                        case RowKind.Buttons: if (_pos.Col < _buttons.Count) _buttons[_pos.Col].Run(); break;
                        case RowKind.Seasons:
                            if (_epEls.Count > 0) { _pos = (2, _memory.TryGetValue(2, out var m) ? Math.Min(m, _epEls.Count - 1) : 0); Draw(); }
                            break;
                        default: Episode(_details.Seasons[_season].Episodes[_pos.Col]); break;
                    }
                    return true;
                case UiAction.HoldOk:
                    if (_rows[_pos.Row] == RowKind.Seasons) SeasonActions(_details.Seasons[_pos.Col]);
                    else if (_rows[_pos.Row] == RowKind.Episodes && _pos.Col < _details.Seasons[_season].Episodes.Count)
                        _ctx.Nav.OpenOverlay(new OverlayRef("itemactions", EpisodeCard(_details.Seasons[_season].Episodes[_pos.Col])));
                    else if (_details.Title.Kind != TitleKind.Episode)
                        _ctx.Nav.OpenOverlay(new OverlayRef("itemactions", Adapters.CatalogueSearch.CardOf(_details.Title)));
                    return true;
            }
            return false;
        }

        /// <summary>Hold OK on a season tab: play it from the start, request it (or what's missing), the show's watchlist.</summary>
        public void SeasonActions(Season s)
        {
            var d = _details!;
            var choices = new List<Choice>();
            var first = s.Episodes.FirstOrDefault(e => e.State == EpisodeState.Available && e.Source != null);
            if (first != null) choices.Add(new($"Play {s.Label.ToLowerInvariant()} from the start", first.Code, () => PlayEpisode(first)));
            var canRequest = _ctx.Shows?.CanRequest == true && d.Title.Seerr != null && !Checking;
            if (canRequest && s.State == SeasonState.Missing) choices.Add(new($"Request {s.Label.ToLowerInvariant()}", $"{s.EpisodeCount} episodes", () => Request(new[] { s.Number })));
            var missing = d.MissingSeasons.ToList();
            if (canRequest && missing.Count > 1) choices.Add(new("Request all missing seasons", ShowDetails.Label(missing), () => Request(missing)));
            if (d.Title.Best != null && _ctx.Shows != null)
                choices.Add(_ctx.Shows.InWatchlist(d.Title)
                    ? new($"Remove {d.Title.Name} from watchlist", "The whole show", ToggleWatchlist)
                    : new($"Add {d.Title.Name} to watchlist", "The whole show (Baird's watchlist holds shows and films, not seasons)", ToggleWatchlist));
            if (choices.Count == 0) { _ctx.Toast($"{s.Label}: {s.StateLabel}."); return; }
            _ctx.Nav.OpenOverlay(new OverlayRef("choices", new ChoiceList($"{d.Title.Name}, {s.Label.ToLowerInvariant()}", s.StateLabel, choices)));
        }

        private void Episode(Episode e)
        {
            switch (e.State)
            {
                case EpisodeState.Available when e.Source != null: PlayEpisode(e); break;
                case EpisodeState.Available: _ctx.Toast($"Seerr says {e.Code} is in your library, but Baird can't find it to play yet."); break;
                case EpisodeState.Requested:
                    _ctx.Toast(e.Download is { } d ? $"{e.Code} is downloading ({d * 100:0}%). It appears in Up next when it's ready." : $"{e.Code} is requested. It appears in Up next when it's ready.");
                    break;
                default:
                    if (Checking) _ctx.Toast($"Still looking for {_title.Name} in your library…");
                    else if (_ctx.Shows?.CanRequest == true && _title.Seerr != null) OpenRequest(e.Season, e.Number);
                    else _ctx.Toast($"{e.Code} isn't streaming, and Baird can't find {_title.Name} in Seerr to request it.");
                    break;
            }
        }

        public void OnShow() { if (_details != null) Draw(); }
        public void Destroy() { _life.Cancel(); _poll?.Dispose(); }
    }

    /// <summary>A lit-able pill button with an icon (the show page's actions).</summary>
    public sealed class PillButton : Border
    {
        private readonly TextBlock _text;
        private readonly Icon? _icon;
        private readonly bool _primary;
        public string Label => _text.Text ?? "";

        public PillButton(string label, string? icon, bool primary)
        {
            _primary = primary;
            Height = 72; CornerRadius = new CornerRadius(999); Padding = new Thickness(30, 0);
            RenderTransformOrigin = RelativePoint.Center;
            _text = new TextBlock { Text = label, FontSize = 27, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            if (icon != null) { _icon = new Icon(icon, 28, T.Text, filled: icon == Icon.Play) { VerticalAlignment = VerticalAlignment.Center }; row.Children.Add(_icon); }
            row.Children.Add(_text);
            Child = row;
            Motion.Animate(this, T.Fast);
            Lit = false;
        }

        public bool Lit
        {
            set
            {
                Classes.Set("focused", value);
                Background = value ? T.FocusFill : _primary ? new SolidColorBrush(Color.Parse("#40FFFFFF")) : T.Pill;
                var ink = value ? T.FocusInk : T.Text;
                _text.Foreground = ink;
                if (_icon != null) _icon.Brush = ink;
                RenderTransform = value ? Motion.Scale(1.06) : Motion.None;
                BoxShadow = value ? T.FocusGlow : default;
            }
        }
    }

    /// <summary>A season tab: "Season 2" over its state ("Jellyfin", "Downloading 40%", "Not streaming").</summary>
    public sealed class SeasonTab : Border
    {
        private readonly TextBlock _name, _state;
        private readonly Season _season;
        private bool _lit, _current;

        public SeasonTab(Season s, bool checking = false)
        {
            _season = s;
            CornerRadius = new CornerRadius(18); Padding = new Thickness(22, 8);
            RenderTransformOrigin = RelativePoint.Center;
            _name = new TextBlock { Text = s.Label, FontSize = 24, FontWeight = FontWeight.SemiBold };
            _state = new TextBlock { Text = checking && s.State == SeasonState.Missing ? "Checking…" : s.StateLabel, FontSize = 18, FontWeight = FontWeight.SemiBold };
            Child = new StackPanel { Children = { _name, _state } };
            Motion.Animate(this, T.Fast);
            Paint();
        }

        public bool Lit { set { _lit = value; Paint(); } }
        public bool Current { set { _current = value; Paint(); } }

        private void Paint()
        {
            Classes.Set("focused", _lit);
            Background = _lit ? T.FocusFill : _current ? new SolidColorBrush(Color.Parse("#24FFFFFF")) : Brushes.Transparent;
            _name.Foreground = _lit ? T.FocusInk : _current ? T.Text : T.Text2;
            _state.Foreground = _lit ? new SolidColorBrush(Color.Parse("#990A0A0C")) : _season.State switch
            {
                SeasonState.Missing => T.Missing,
                SeasonState.Requested => T.Downloading,
                _ => T.Text3,
            };
            RenderTransform = _lit ? Motion.Scale(1.06) : Motion.None;
            BoxShadow = _lit ? T.FocusGlow : default;
        }
    }
}
