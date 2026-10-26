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
using Baird.NewUi.Playback;
using Baird.NewUi.Shell;
using Baird.NewUi.Theme;

namespace Baird.NewUi.Screens
{
    /// <summary>
    /// One search for everything (prototype: screens/search.js): the on-screen keyboard on the left, results on the
    /// right in their own clipped column that scrolls vertically (never under the keyboard). Digits and DEL on the
    /// remote type too. Results: top result, shows and films (one per title, wherever it streams, or to request),
    /// channels, and what's on TV. With nothing typed: recent searches and popular titles to request.
    /// </summary>
    public sealed class SearchScreen : IScreen
    {
        public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(450);
        private enum Zone { Keyboard, Results, None }
        private enum Shape { Top, Poster, Wide, Chip }

        private static readonly Dictionary<Shape, int> PerRow = new() { [Shape.Top] = 1, [Shape.Poster] = 4, [Shape.Wide] = 3, [Shape.Chip] = 3 };
        private static readonly Dictionary<Shape, double> TrackH = new() { [Shape.Top] = 356, [Shape.Poster] = 410, [Shape.Wide] = 262, [Shape.Chip] = 76 };
        private static readonly Size WideSize = new(312, 176), TopSize = new(480, 270);
        private const double TitleH = 52, RowGap = 26, ColumnTop = 120, ColumnLeft = 850, ColumnW = 1050, Pad = 22;
        private const double ViewH = 1030 - ColumnTop; // the lit row stays above the safe bottom

        private sealed class Row
        {
            public required string? Title;
            public required Shape Shape;
            public required List<SearchHit> Hits;
            public List<Control> Els = new();
            public Control View = null!;
            public double Top, Height;
        }

        private readonly NewUiContext _ctx;
        private readonly ITimers _timers;
        private readonly Panel _root = new() { Width = 1920, Height = 1080 };
        private readonly TextBlock _queryText = new() { FontSize = 36, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 560 };
        // The caret sits right after the last letter, or before the hint while nothing is typed.
        private readonly Border _caret = new() { Width = 3, Height = 40, Background = T.Text, VerticalAlignment = VerticalAlignment.Center };
        private readonly StackPanel _entry = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        internal TextBlock QueryText => _queryText;
        internal Border Caret => _caret;
        private readonly TextBlock _status = new() { FontSize = T.Small, Foreground = T.Text2, TextWrapping = TextWrapping.Wrap, Width = 700 };
        /// <summary>"Nothing called …": in the results column, where the results would be.</summary>
        private readonly TextBlock _empty = new() { FontSize = 30, Foreground = T.Text2, TextWrapping = TextWrapping.Wrap, MaxWidth = 760, LineHeight = 42, Margin = new Thickness(Pad, 40, 0, 0), IsVisible = false };
        public TextBlock EmptyMessage => _empty;
        private readonly OnScreenKeyboard _kb = new(700);
        private readonly StackPanel _inner = new() { Spacing = RowGap, Margin = new Thickness(Pad, Pad, Pad, 0) };
        private readonly Border _column;
        private List<Row> _rows = new();
        private (int Row, int Col) _pos;
        private readonly Dictionary<int, int> _memory = new();
        private Zone _zone = Zone.Keyboard;
        private string _query = "";
        private string _shownQuery = "\0";
        private IDisposable? _debounce;
        private CancellationTokenSource? _cts;
        private bool _searching;
        private double _scroll;
        private IReadOnlyList<string> _recent = Array.Empty<string>();
        private IReadOnlyList<HomeCard> _popular = Array.Empty<HomeCard>();

        public Control View => _root;
        public string Query => _query;
        public Control? LitElement => _zone switch
        {
            Zone.Keyboard => _kb.CurrentKey,
            Zone.Results when _rows.Count > 0 => _rows[_pos.Row].Els[_pos.Col],
            _ => null,
        };
        /// <summary>The hits shown, row by row (tests).</summary>
        public IReadOnlyList<IReadOnlyList<SearchHit>> ResultRows => _rows.Select(r => (IReadOnlyList<SearchHit>)r.Hits).ToList();
        public double ResultsScroll => _scroll;

        public SearchScreen(NewUiContext ctx, ITimers? timers = null)
        {
            _ctx = ctx;
            _timers = timers ?? ctx.Timers;
            _root.Background = new RadialGradientBrush
            {
                Center = new RelativePoint(0.85, 0, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.85, 0, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.8, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.7, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#38505A8C"), 0), new GradientStop(Colors.Black, 0.6) },
            };

            var field = new Border
            {
                Height = 88,
                CornerRadius = new CornerRadius(22),
                Background = new SolidColorBrush(Color.Parse("#1AFFFFFF")),
                Padding = new Thickness(28, 0),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 18,
                    Children = { new Icon(Icon.Search, 34, T.Text2) { VerticalAlignment = VerticalAlignment.Center }, _entry },
                },
            };
            var left = new StackPanel { Width = 700, Spacing = 30, Margin = new Thickness(T.SafeX, 140, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Children = { field, _kb, _status } };
            _kb.Pressed += Key;

            Motion.Animate(_inner, Motion.Med);
            _column = new Border
            {
                Width = ColumnW,
                Height = 1080 - ColumnTop,
                ClipToBounds = true,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(ColumnLeft, ColumnTop, 0, 0),
                Child = new Panel { Children = { _inner, _empty } },
            };
            _root.Children.Add(left);
            _root.Children.Add(_column);
            DrawField();
            Build();
        }

        // ------------------------------------------------------------------ typing
        private void Key(string k)
        {
            if (k == "voice") { Voice(); return; }
            Type(k == "space" ? " " : k == "del" ? null : k);
        }

        private void Type(string? text)
        {
            if (text == null) { if (_query.Length == 0) return; _query = _query[..^1]; }
            else { if (_query.Length >= 60) return; _query += text; }
            Changed();
        }

        public bool AcceptsText => true;

        /// <summary>A real keyboard types straight into the query, wherever the remote's focus is on this screen.</summary>
        public void TypeText(string? text)
        {
            if (_zone != Zone.Keyboard) { _zone = Zone.Keyboard; Draw(); }
            Type(text);
        }

        private void Voice() => _ctx.Toast("Hold the remote's voice button and speak, then let go. (It needs VOICE_COMMAND_KEY in config.ini.) Or type with the keyboard or the remote's digits.");

        /// <summary>Set the query (and search straight away), e.g. from a recent search.</summary>
        public void SetQuery(string q)
        {
            _query = q;
            DrawField();
            _debounce?.Dispose();
            _ = RunAsync();
        }

        private void Changed()
        {
            DrawField();
            _debounce?.Dispose();
            _debounce = _timers.After(Debounce, () => _ = RunAsync());
        }

        private void DrawField()
        {
            _queryText.Text = _query.Length > 0 ? _query : "Shows, films, channels, what's on";
            _queryText.Foreground = _query.Length > 0 ? T.Text : T.Text3;
            _queryText.FontSize = _query.Length > 0 ? 36 : 30;
            _entry.Children.Clear();
            if (_query.Length > 0) { _caret.Margin = new Thickness(2, 0, 0, 0); _entry.Children.Add(_queryText); _entry.Children.Add(_caret); }
            else { _caret.Margin = new Thickness(0, 0, 10, 0); _entry.Children.Add(_caret); _entry.Children.Add(_queryText); }
            var nothing = !_searching && _query.Trim().Length > 0 && _rows.Count == 0 && _shownQuery == _query.Trim();
            _empty.IsVisible = nothing;
            if (nothing) _empty.Text = $"Nothing called “{_query.Trim()}” yet. Try fewer letters, or a channel number.";
            _status.Text = _searching ? Waiting() : _query.Length > 0 && _ctx.SearchNote() is { } note ? note : "Digits and DEL on the remote type too, and so does a keyboard. ▸ from the keyboard reaches the results.";
        }

        /// <summary>While results are still arriving: which library sources haven't answered yet.</summary>
        private string Waiting()
        {
            var pending = _results.Query == _query.Trim() ? _results.Pending : Array.Empty<string>();
            return pending.Count == 0 ? "Searching…" : $"Still checking {string.Join(", ", pending.Select(n => n == "TvHeadend" ? "Live TV" : n))}…";
        }

        /// <summary>Runs the search for the current query (the debounce has passed); results show as each source answers. Tests await it.</summary>
        public async Task RunAsync()
        {
            var q = _query.Trim();
            _cts?.Cancel();
            if (q.Length == 0) { _searching = false; Show(SearchResults.Empty); return; }
            if (_ctx.Search == null) { Show(SearchResults.Empty); return; }
            var cts = _cts = new CancellationTokenSource();
            _searching = true; DrawField();
            try
            {
                var r = await _ctx.Search.SearchAsync(q, _ctx.Channels, _ctx.Now(),
                    partial => OnUi(() => { if (!cts.IsCancellationRequested && _query.Trim() == q) Show(partial); }), cts.Token);
                if (cts.IsCancellationRequested) return;
                OnUi(() => { if (!cts.IsCancellationRequested && _query.Trim() == q) { _searching = false; Show(r); } });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[Search] '{q}' failed: {ex.Message}");
                OnUi(() => { _searching = false; DrawField(); });
            }
        }

        private static void OnUi(Action a) { if (Dispatcher.UIThread.CheckAccess()) a(); else Dispatcher.UIThread.Post(a); }

        private SearchResults _results = SearchResults.Empty;
        private void Show(SearchResults r) { _results = r; Build(); }

        /// <summary>Who a hit is, across updates (a title keeps its identity as more sources for it arrive).</summary>
        private static string KeyOf(SearchHit h) => h.Title is { } t
            ? Adapters.CatalogueSearch.IdentityOf(t)
            : $"{h.Kind}:{h.Card.Id}";

        private string _signature = "\0";
        private string _builtFor = "\0";

        // ------------------------------------------------------------------ results
        private void Build()
        {
            var q = _query.Trim();
            var sections = new List<(string Title, Shape Shape, List<SearchHit> Hits)>();
            if (q.Length == 0 || _results.Query.Length == 0)
            {
                if (q.Length == 0)
                {
                    if (_recent.Count > 0) sections.Add(("Recent searches", Shape.Chip, _recent.Select(t => new SearchHit(HitKind.Recent, new HomeCard(CardKind.Film, "recent:" + t, t, null, null, null, null, "", PlayVia.None))).ToList()));
                    if (_popular.Count > 0) sections.Add(("Popular to request", Shape.Poster, _popular.Select(c => new SearchHit(HitKind.Title, c)).ToList()));
                }
            }
            else
            {
                if (_results.Top != null) sections.Add(("Top result", Shape.Top, new() { _results.Top }));
                if (_results.Titles.Count > 0) sections.Add(("Shows and films", Shape.Poster, _results.Titles.Take(24).ToList()));
                if (_results.Channels.Count > 0) sections.Add(("Channels", Shape.Wide, _results.Channels.Take(9).ToList()));
                if (_results.OnTv.Count > 0) sections.Add(("On TV", Shape.Wide, _results.OnTv.Take(9).ToList()));
            }
            _shownQuery = _results.Query.Length > 0 ? _results.Query : q.Length == 0 ? "" : _shownQuery;

            // Nothing new (e.g. a source answered with nothing): leave the cards alone, so nothing flickers.
            var signature = string.Join("|", sections.Select(sec => $"{sec.Title}:{string.Join(",", sec.Hits.Select(h => $"{KeyOf(h)}/{h.Card.Id}/{h.Card.Sublabel}/{h.Card.Badge}/{h.Card.Programme?.Id}"))}"));
            if (signature == _signature) { DrawField(); return; }
            _signature = signature;
            // The same query with more results: keep the lit result lit wherever it has moved to (never yank focus).
            var sameQuery = _shownQuery == _builtFor;
            var litKey = sameQuery && _zone == Zone.Results && _rows.Count > 0 ? KeyOf(_rows[_pos.Row].Hits[_pos.Col]) : null;
            var oldPos = _pos;
            _builtFor = _shownQuery;

            _rows = new();
            var y = 0.0;
            foreach (var (title, shape, hits) in sections)
                for (var k = 0; k < hits.Count; k += PerRow[shape])
                {
                    var row = new Row { Title = k == 0 ? title : null, Shape = shape, Hits = hits.Skip(k).Take(PerRow[shape]).ToList() };
                    row.Els = row.Hits.Select(h => Element(h, shape)).ToList();
                    var track = new StackPanel { Orientation = Orientation.Horizontal, Spacing = shape == Shape.Chip ? 14 : 36, Height = TrackH[shape] };
                    foreach (var e in row.Els) track.Children.Add(e);
                    if (shape == Shape.Top) track.Children.Add(TopText(row.Hits[0]));
                    var view = new StackPanel();
                    if (row.Title != null) view.Children.Add(new TextBlock { Text = row.Title, FontSize = 28, FontWeight = FontWeight.SemiBold, Foreground = T.Text2, Height = TitleH });
                    view.Children.Add(track);
                    row.Height = (row.Title != null ? TitleH : 0) + TrackH[shape];
                    view.Height = row.Height;
                    row.View = view;
                    row.Top = y;
                    y += row.Height + RowGap;
                    _rows.Add(row);
                }
            _inner.Children.Clear();
            foreach (var r in _rows) _inner.Children.Add(r.View);
            _pos = (0, 0);
            if (!sameQuery) _memory.Clear();
            if (sameQuery && _rows.Count > 0)
            {
                var at = _rows.SelectMany((r, i) => r.Hits.Select((h, j) => (i, j, h))).FirstOrDefault(x => KeyOf(x.h) == litKey);
                if (litKey != null && at.h != null) _pos = (at.i, at.j);
                else
                {
                    var row = Math.Min(oldPos.Row, _rows.Count - 1);
                    _pos = (row, Math.Min(oldPos.Col, _rows[row].Els.Count - 1));
                }
            }
            if (_rows.Count == 0 && _zone == Zone.Results) _zone = Zone.Keyboard; // nothing to the right: stay on the keyboard
            DrawField();
            Draw();
        }

        private Control Element(SearchHit h, Shape shape) => shape switch
        {
            Shape.Chip => new Chip(h.Card.Label),
            Shape.Top => TopCard(h),
            Shape.Poster => new Card(h.Card, CardShape.Poster),
            _ => new Card(h.Card, CardShape.Wide, WideSize),
        };

        /// <summary>What the top result's pill says: "▶ Play", "⤓ Request", "Requested", "Live", a time.</summary>
        public static (string Text, bool Live, bool Get) TopPill(SearchHit h, DateTimeOffset now)
        {
            if (h.Kind == HitKind.Channel) return ("Live", true, false);
            if (h.Kind == HitKind.Listing && h.Card.Programme is { } p) return p.IsOnAt(now) ? ("Live", true, false) : ($"{Adapters.CatalogueSearch.When(p, now)}", false, false);
            if (h.Title is { } t && t.Best == null)
                return t.Availability switch
                {
                    Availability.Get => ("⤓ Request", false, true),
                    Availability.Requested => ("Requested", false, false),
                    Availability.Partial or Availability.Library => ("In your library", false, false),
                    _ => (h.Card.Sublabel?.EndsWith(ShowDetails.Checking) == true ? ShowDetails.Checking : "Not streaming", false, false),
                };
            return ("▶ Play", false, false);
        }

        /// <summary>The top result: big art with its name set on it and a pill saying what OK does (prototype: search.js top-card).</summary>
        private Control TopCard(SearchHit h)
        {
            var card = new Card(h.Card with { Badge = null }, CardShape.Wide, TopSize, fallbackTitle: false); // the name is set on it below
            var (text, live, get) = TopPill(h, _ctx.Now());
            card.Overlay(new Border { Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 0.35, RelativeUnit.Relative), GradientStops = { new GradientStop(Color.Parse("#D0000000"), 0), new GradientStop(Color.Parse("#00000000"), 1) } } });
            var name = h.Card.Label;
            card.Overlay(new StackPanel
            {
                Spacing = 10,
                Margin = new Thickness(22, 0, 22, 20),
                VerticalAlignment = VerticalAlignment.Bottom,
                Children =
                {
                    new Border
                    {
                        HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(999), Padding = new Thickness(14, 5),
                        Background = live ? T.Live : get ? T.FocusFill : T.Pill,
                        Child = new TextBlock { Text = text, FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = get ? T.FocusInk : T.Text },
                    },
                    new TextBlock { Text = name, FontSize = 40, FontWeight = FontWeight.Bold, Foreground = T.Text, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.WordEllipsis, IsVisible = h.Card.Kind != CardKind.Channel }, // a channel card names itself
                },
            });
            return card;
        }

        /// <summary>The top result's pill (tests).</summary>
        public string? TopPillText => _rows.Count > 0 && _rows[0].Shape == Shape.Top ? TopPill(_rows[0].Hits[0], _ctx.Now()).Text : null;

        private static Control TopText(SearchHit h)
        {
            var c = h.Card;
            var panel = new StackPanel { Width = 440, Spacing = 14, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 40, 0, 0) };
            string kind = h.Kind switch
            {
                HitKind.Channel => $"Channel {c.Channel?.Number}",
                HitKind.Listing => "On TV",
                _ => h.Title?.Kind switch { TitleKind.Series => "Series", TitleKind.Episode => "Episode", _ => "Film" },
            };
            panel.Children.Add(new TextBlock { Text = kind, FontSize = T.Small, FontWeight = FontWeight.SemiBold, Foreground = c.Badge == "live" ? T.Live : T.Text2 });
            var where = h.Kind == HitKind.Channel ? c.Programme is { } p ? $"Now: {p.Title}" : "No programme information" : h.Title?.Where ?? c.Sublabel;
            if (where != null) panel.Children.Add(new TextBlock { Text = where, FontSize = T.Body, FontWeight = FontWeight.SemiBold, Foreground = T.Text, TextWrapping = TextWrapping.Wrap });
            if (c.Synopsis is { Length: > 0 } syn) panel.Children.Add(new TextBlock { Text = syn, FontSize = T.Small, Foreground = T.Text2, TextWrapping = TextWrapping.Wrap, MaxLines = 4, TextTrimming = TextTrimming.WordEllipsis });
            return panel;
        }

        private void Draw()
        {
            _kb.Draw(_zone == Zone.Keyboard);
            for (var i = 0; i < _rows.Count; i++)
                for (var j = 0; j < _rows[i].Els.Count; j++)
                {
                    var lit = _zone == Zone.Results && i == _pos.Row && j == _pos.Col;
                    var dim = _zone == Zone.Results && i == _pos.Row && !lit;
                    switch (_rows[i].Els[j])
                    {
                        case Card c: c.Lit = lit; c.Dimmed = dim; break;
                        case Chip ch: ch.Lit = lit; break;
                    }
                }
            // Scroll the column just enough that the lit row (with its title, and room for the lift) is in view.
            var y = _scroll;
            if (_zone != Zone.Results || _rows.Count == 0) y = 0;
            else
            {
                var r = _rows[_pos.Row];
                var lift = (r.Height - (r.Title != null ? TitleH : 0)) * 0.07 + 8;
                var top = r.Top + Pad - (r.Title != null ? 0 : lift);
                var bottom = r.Top + Pad + r.Height + lift;
                if (top + y < 0) y = -top;
                if (bottom + y > ViewH) y = ViewH - bottom;
            }
            _scroll = y;
            _inner.RenderTransform = TransformOperations.Parse($"translate(0px, {y}px)");
            // The lit result's page gets ready while it's lit (artwork too), so opening it draws at once.
            _ctx.Prefetch?.Lit(_zone == Zone.Results && _rows.Count > 0 ? _rows[_pos.Row].Hits[_pos.Col].Card : null);
        }

        // ------------------------------------------------------------------ keys
        public bool OnAction(UiAction a, bool repeat)
        {
            if (_zone == Zone.None) _zone = Zone.Keyboard;
            if (InputMap.DigitOf(a) is { } d) { Type(d.ToString()); return true; }
            switch (a)
            {
                case UiAction.Del: Type(null); return true;
                case UiAction.Voice: Voice(); return true;
            }
            if (_zone == Zone.Keyboard)
            {
                switch (_kb.OnAction(a))
                {
                    case OnScreenKeyboard.Outcome.ExitUp: return false; // the shell lights the tab bar
                    case OnScreenKeyboard.Outcome.ExitRight when _rows.Count > 0:
                        _zone = Zone.Results; _pos = (_pos.Row, 0); Draw(); return true;
                    case OnScreenKeyboard.Outcome.Ignored: return a == UiAction.HoldOk || a == UiAction.PlayPause;
                    default: return true;
                }
            }
            if (_rows.Count == 0) { _zone = Zone.Keyboard; Draw(); return true; }
            switch (a)
            {
                case UiAction.Left when _pos.Col == 0: _zone = Zone.Keyboard; Draw(); return true;
                case UiAction.Up when _pos.Row == 0: return false;
                case UiAction.Up or UiAction.Down or UiAction.Left or UiAction.Right:
                    var dir = a switch { UiAction.Up => Dir.Up, UiAction.Down => Dir.Down, UiAction.Left => Dir.Left, _ => Dir.Right };
                    _pos = FocusMath.MoveInRows(_rows.Select(r => r.Els.Count).ToList(), _pos, dir, _memory);
                    Draw();
                    return true;
                case UiAction.Ok or UiAction.PlayPause or UiAction.HoldOk:
                    Activate(_rows[_pos.Row].Hits[_pos.Col], a == UiAction.HoldOk);
                    return true;
            }
            return false;
        }

        private void Activate(SearchHit h, bool more)
        {
            if (h.Kind == HitKind.Recent) { _zone = Zone.Keyboard; SetQuery(h.Card.Label); return; }
            _ctx.Search?.Remember(_query);
            if (more) { Actions(h); return; }
            switch (h.Kind)
            {
                case HitKind.Channel when h.Card.Channel != null:
                    _ctx.PlayLive(h.Card.Channel); return;
                case HitKind.Listing when h.Card.Channel != null && h.Card.Programme is { } p:
                    // On now: watch it. Later: the guide, lit on it (Remind me is there).
                    if (p.IsOnAt(_ctx.Now())) _ctx.PlayLive(h.Card.Channel);
                    else _ctx.OpenGuide(h.Card.Channel.Id, p.Start);
                    return;
            }
            // A title: a series (or one that nothing can play) opens its page; a film or episode plays.
            if (h.Title is { } t && (t.Kind == TitleKind.Series || t.Best == null)) { _ctx.OpenShow(t); return; }
            _ctx.Activate(h.Card);
        }

        /// <summary>Hold OK: a title's or channel's actions; a listing's choices (watch it now, or find it in the guide to be reminded).</summary>
        private void Actions(SearchHit h)
        {
            if (h.Kind == HitKind.Listing && h.Card.Channel is { } ch && h.Card.Programme is { } p)
            {
                var choices = new List<Choice>();
                if (p.IsOnAt(_ctx.Now())) choices.Add(new($"Watch now on {ch.Name}", ch.Number, () => _ctx.PlayLive(ch)));
                choices.Add(new("Show it in the guide", p.IsOnAt(_ctx.Now()) ? "What's on around it" : "Set a reminder there", () => _ctx.OpenGuide(ch.Id, p.Start)));
                _ctx.Nav.OpenOverlay(new OverlayRef("choices", new ChoiceList(p.Title, $"{ch.Name} · {Adapters.CatalogueSearch.When(p, _ctx.Now())}", choices)));
                return;
            }
            _ctx.Nav.OpenOverlay(new OverlayRef("itemactions", h.Card));
        }

        public async void OnShow()
        {
            if (_query.Trim().Length > 0) return;
            try
            {
                var recent = _ctx.Search?.RecentAsync() ?? Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
                var popular = _ctx.Home.DiscoverAsync();
                await Task.WhenAll(recent, popular);
                OnUi(() =>
                {
                    _recent = recent.Result;
                    _popular = popular.Result.Where(c => c.Badge == "get").Take(8).ToList();
                    if (_query.Trim().Length == 0) Build();
                });
            }
            catch (Exception ex) { Console.WriteLine($"[Search] Suggestions failed: {ex.Message}"); }
        }

        public void OnFocusIn() { _zone = Zone.Keyboard; Draw(); }
        public void OnFocusOut() { _zone = Zone.None; Draw(); }
        public void Destroy() { _debounce?.Dispose(); _cts?.Cancel(); }
    }

    /// <summary>A lit-able pill (recent searches).</summary>
    public sealed class Chip : Border
    {
        private readonly TextBlock _text;
        public Chip(string text)
        {
            Height = 60; CornerRadius = new CornerRadius(999); Padding = new Thickness(28, 0); VerticalAlignment = VerticalAlignment.Top;
            Background = T.Pill;
            RenderTransformOrigin = RelativePoint.Center;
            _text = new TextBlock { Text = text, FontSize = 26, FontWeight = FontWeight.SemiBold, Foreground = T.Text, VerticalAlignment = VerticalAlignment.Center };
            Child = _text;
            Motion.Animate(this, T.Fast);
        }
        public bool Lit
        {
            set
            {
                Classes.Set("focused", value);
                Background = value ? T.FocusFill : T.Pill;
                _text.Foreground = value ? T.FocusInk : T.Text;
                RenderTransform = value ? Motion.Scale(1.06) : Motion.None;
                BoxShadow = value ? T.FocusGlow : default;
            }
        }
    }
}
