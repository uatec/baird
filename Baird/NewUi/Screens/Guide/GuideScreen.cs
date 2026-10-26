using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Baird.NewUi.Adapters;
using Baird.NewUi.Core;
using Baird.NewUi.Model;
using Baird.NewUi.Playback;
using Baird.NewUi.Theme;

namespace Baird.NewUi.Screens
{
    /// <summary>
    /// Live TV: the guide (prototype js/screens/guide.js, "channels down" layout). ◂ ▸ move through time, ▴ ▾ through
    /// channels keeping the time you're in, Pg± a page of channels, digits jump to a channel, ⏮ ⏭ jump between
    /// Now / This evening / Tomorrow. OK on now (or a channel with no information) tunes; OK later says when it's on.
    /// Hold OK = actions (Watch, Remind me, All showings, Open the series). ▴ off the top row returns false: the shell
    /// lights the tab bar.
    /// </summary>
    public sealed class GuideScreen : IScreen
    {
        public const int VisibleChannels = GuideGridView.Visible;
        public const double BodyTop = GuideGridView.ChannelsTop, TimeStart = GuideGridView.TimeStart;
        private static readonly TimeSpan Refresh = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan Stale = TimeSpan.FromMinutes(20);

        private readonly NewUiContext _ctx;
        private readonly Func<DateTimeOffset, Task<GuideModel>> _load;
        private readonly ITimers _timers;
        private readonly Panel _root = new() { Width = 1920, Height = 1080, Background = Brushes.Transparent };
        private readonly GuideHeader _header = new();
        private readonly GuideGridView _grid = new();
        private readonly GuideActions _actions = new();
        private readonly TextBlock _status = new() { FontSize = 30, Foreground = T.Text2, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(GuideGridView.TimeStart, GuideGridView.ChannelsTop + 20, 0, 0) };
        private readonly DispatcherTimer _tick = new() { Interval = Refresh };

        private GuideModel? _model;
        private GuideGrid? _g;
        private DateTimeOffset _loadedAt;
        private GuidePos _pos = new(0, "", default);
        private bool _focused = true;
        private bool _loading;
        private string? _shownForChannel;
        private NumberEntry? _entry;
        private IDisposable? _entryTimer;

        public GuideScreen(NewUiContext ctx, Func<DateTimeOffset, Task<GuideModel>> load, ITimers timers)
        {
            _ctx = ctx; _load = load; _timers = timers;
            _root.Children.Add(_header);
            _root.Children.Add(_grid);
            _root.Children.Add(_status);
            _root.Children.Add(_actions);
            _status.Text = "Loading the guide…";
            _tick.Tick += (_, _) => { if (_root.IsEffectivelyVisible) Draw(); };
            _root.AttachedToVisualTree += (_, _) => _tick.Start();
            _root.DetachedFromVisualTree += (_, _) => _tick.Stop();
            ctx.Playback.Changed += () => { if (_model != null) Draw(); };
        }

        public Control View => _root;
        public Control? LitElement => _actions.IsOpen ? _actions.Lit : _focused && _g != null ? _grid.Highlight : null;

        // For tests and the shell.
        public GuidePos Pos => _pos;
        public Programme Lit => GuideFocus.Lit(_g!, _pos);
        public int RealisedCells => _grid.RealisedCells;
        public string EntryText => _header.EntryText;
        public bool ActionsOpen => _actions.IsOpen;
        public IReadOnlyList<string> ActionLabels => _actions.Labels;
        public void RunAction(string label) => _actions.Run(label);

        /// <summary>Whether the shell should frame the playing video into the guide's preview.</summary>
        public static bool ShowsPreview(NavState st, bool playing) =>
            playing && st.Place == Place.LiveTv && st.Pages.Count == 0 && !(st.Video != null && st.AtVideo);

        /// <summary>Scale and move a full-window surface of <paramref name="surface"/> size onto <paramref name="target"/> (origin top-left).</summary>
        public static ITransform PreviewTransform(Rect target, Size surface)
        {
            var b = Avalonia.Media.Transformation.TransformOperations.CreateBuilder(1);
            b.AppendMatrix(Matrix.CreateScale(target.Width / surface.Width, target.Height / surface.Height) * Matrix.CreateTranslation(target.X, target.Y));
            return b.Build();
        }

        public async Task LoadAsync()
        {
            if (_loading) return;
            _loading = true;
            try
            {
                var keep = _model != null && _g != null ? (Channel: _model.Channels[_pos.Ch].Channel.Id, _pos.Anchor) : default;
                var model = await _load(_ctx.Now());
                _model = model;
                _loadedAt = _ctx.Now();
                _grid.SetModel(model);
                if (model.Channels.Count == 0) { _g = null; _status.Text = "No channels yet. Check TvHeadend in Settings."; return; }
                _status.Text = "";
                if (_ctx.Channels.Count == 0) _ctx.Channels = model.Channels.Select(c => c.Channel).ToList();
                _g = new GuideGrid(model.Channels.Select(c => c.Schedule).ToList(), model.Start, _ctx.Now());
                var ch = keep.Channel != null ? Math.Max(0, IndexOf(keep.Channel)) : Math.Max(0, IndexOf(_ctx.Playback.Current?.Channel?.Id));
                var anchor = keep.Channel != null && keep.Anchor > _ctx.Now() ? keep.Anchor : _ctx.Now();
                _pos = GuideFocus.ToChannel(_g, ch, anchor);
                _shownForChannel = _ctx.Playback.Current?.Channel?.Id;
                Draw();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Guide] Load failed: {ex.Message}");
                _status.Text = "The guide couldn't load. Try again in a moment.";
            }
            finally { _loading = false; }
        }

        public async void OnShow()
        {
            if (_model == null || _ctx.Now() - _loadedAt > Stale) { await LoadAsync(); return; }
            // Coming back while a different channel plays: start on that channel, now.
            var playing = _ctx.Playback.Current?.Channel?.Id;
            if (playing != null && playing != _shownForChannel && IndexOf(playing) is var i and >= 0)
            {
                _shownForChannel = playing;
                _pos = GuideFocus.ToChannel(_g!, i, _ctx.Now());
            }
            Draw();
        }

        public void OnFocusIn() { _focused = true; Draw(); }
        public void OnFocusOut() { _focused = false; _actions.Close(); Draw(); }

        public bool OnAction(UiAction a, bool repeat)
        {
            if (_actions.IsOpen) { _actions.OnAction(a); Draw(); return true; }
            if (_g == null) return false;
            _focused = true;
            if (InputMap.DigitOf(a) is { } d) { Digit(d); return true; }
            switch (a)
            {
                case UiAction.Up when _pos.Ch == 0: return false; // the shell lights the tab bar
                case UiAction.Up: Move(GuideMove.Up); return true;
                case UiAction.Down: Move(GuideMove.Down); return true;
                case UiAction.Left: Move(GuideMove.Left); return true;
                case UiAction.Right: Move(GuideMove.Right); return true;
                case UiAction.PageUp: Move(GuideMove.PageUp); return true;
                case UiAction.PageDown: Move(GuideMove.PageDown); return true;
                case UiAction.Prev or UiAction.Next: JumpStop(a == UiAction.Next ? 1 : -1); return true;
                case UiAction.Del when _entry != null: { var r = _entry.Del(); if (r.Text.Length == 0) EndEntry(); else ShowEntry(r); return true; }
                case UiAction.Back when _entry != null: EndEntry(); return true;
                case UiAction.Ok or UiAction.PlayPause: Activate(Lit); return true;
                case UiAction.HoldOk: OpenActions(Lit); return true;
            }
            return false;
        }

        private void Move(GuideMove m) { _g = _g! with { Now = _ctx.Now() }; _pos = GuideFocus.Move(_g, _pos, m, VisibleChannels); Draw(); }

        private int IndexOf(string? channelId)
        {
            if (channelId == null || _model == null) return -1;
            for (var i = 0; i < _model.Channels.Count; i++) if (_model.Channels[i].Channel.Id == channelId) return i;
            return -1;
        }

        private Channel ChannelOf(Programme p) => _model!.Channels.First(c => c.Channel.Id == p.ChannelId).Channel;

        // ------------------------------------------------------------------ OK and actions
        private void Activate(Programme p)
        {
            var now = _ctx.Now();
            var ch = ChannelOf(p);
            if (p.NoInfo || p.IsOnAt(now)) { _ctx.PlayLive(ch); return; }
            if (p.End <= now) { _ctx.Toast($"{p.Title} finished at {GuideHeader.Hhmm(p.End)}. There's no catch-up for {ch.Name}."); return; }
            Remind(p, ch);
        }

        /// <summary>Reminders (countdown card, switch at the start) aren't built yet: say plainly when it's on instead.</summary>
        private void Remind(Programme p, Channel ch) =>
            _ctx.Toast($"{p.Title} is on {GuideHeader.DayWord(p.Start, _ctx.Now()).ToLowerInvariant()} at {GuideHeader.Hhmm(p.Start)} on {ch.Name}. Reminders arrive in a later phase.");

        private void OpenActions(Programme p)
        {
            var now = _ctx.Now();
            var ch = ChannelOf(p);
            var items = new List<GuideAction>();
            if (p.NoInfo || p.IsOnAt(now)) items.Add(new("Watch now", ch.Name, () => { _actions.Close(); _ctx.PlayLive(ch); }));
            if (!p.NoInfo && p.Start > now) items.Add(new("Remind me", $"{GuideHeader.DayWord(p.Start, now)} {GuideHeader.Hhmm(p.Start)}", () => { _actions.Close(); Remind(p, ch); }));
            if (!p.NoInfo)
            {
                items.Add(new("All showings", "Other times and channels", () => OpenShowings(p)));
                items.Add(new("Open the series", "Arrives with show pages", () => { }, Enabled: false));
            }
            _actions.Open(p.NoInfo ? ch.Name : p.Title, p.NoInfo ? "No information" : $"{ch.Name}, {GuideHeader.Hhmm(p.Start)}–{GuideHeader.Hhmm(p.End)}", items);
        }

        /// <summary>Every showing of this title from now on, across the channels and the day the guide holds; OK glides there.</summary>
        private void OpenShowings(Programme p)
        {
            var now = _ctx.Now();
            var showings = _model!.Channels
                .SelectMany((c, i) => c.Schedule.Where(x => !x.NoInfo && x.End > now && string.Equals(x.Title, p.Title, StringComparison.OrdinalIgnoreCase)).Select(x => (Row: i, P: x, Ch: c.Channel)))
                .OrderBy(s => s.P.Start)
                .ToList();
            var seen = new HashSet<string>();
            var items = showings.Select(s =>
            {
                var key = s.P.Subtitle ?? s.P.Title;
                var repeat = !seen.Add(key) && s.P.Subtitle != null;
                var label = $"{GuideHeader.DayWord(s.P.Start, now)} {GuideHeader.Hhmm(s.P.Start)}  ·  {s.Ch.Name}";
                var detail = s.P.IsOnAt(now) ? "On now" : repeat ? "Repeat" : s.P.Subtitle;
                return new GuideAction(label, detail, () =>
                {
                    _actions.Close();
                    _pos = new GuidePos(s.Row, s.P.Id, s.P.IsOnAt(now) ? now : s.P.Start);
                    Draw();
                });
            }).ToList();
            _actions.Open(p.Title, showings.Count == 1 ? "This is the only showing in the guide" : $"{showings.Count} showings in the next day", items);
        }

        // ------------------------------------------------------------------ number entry
        private void Digit(int d)
        {
            _entry ??= new NumberEntry(_model!.Channels.Select(c => c.Channel.Number));
            var r = _entry.Push(d);
            ShowEntry(r);
            _entryTimer?.Dispose();
            _entryTimer = _timers.After(_entry.IsUnique ? TimeSpan.FromMilliseconds(400) : TimeSpan.FromMilliseconds(r.Error != null ? 1400 : 1500), CommitEntry);
        }

        private void ShowEntry(NumberEntry.Result r)
        {
            var name = r.Match != null ? _model!.Channels.First(c => c.Channel.Number == r.Match).Channel.Name : null;
            _header.ShowEntry($"{r.Text}{new string('_', Math.Max(0, 3 - r.Text.Length))}{(r.Error != null ? $"   {r.Error}" : name != null ? $"   {name}" : "")}", true);
        }

        private void CommitEntry()
        {
            var r = _entry?.State();
            if (r?.Match is { } number && IndexOf(_model!.Channels.First(c => c.Channel.Number == number).Channel.Id) is var i and >= 0)
            {
                _pos = GuideFocus.ToChannel(_g!, i, _pos.Anchor);
                Draw();
            }
            EndEntry();
        }

        private void EndEntry()
        {
            _entryTimer?.Dispose();
            _entry = null;
            _header.ShowEntry("", false);
        }

        // ------------------------------------------------------------------ time jumps (the strip)
        private IReadOnlyList<(string Label, DateTimeOffset At)> Stops()
        {
            var now = _ctx.Now();
            var local = now.ToLocalTime();
            DateTimeOffset At(int day, int hour) => new(local.Date.AddDays(day).AddHours(hour), local.Offset);
            var list = new List<(string, DateTimeOffset)> { ("Now", now), ("This evening", At(local.Hour >= 21 ? 1 : 0, 19)), ("Tomorrow morning", At(1, 8)), ("Tomorrow evening", At(1, 19)) };
            return list.Where(s => s.Item2 < _model!.End).ToList();
        }

        private void JumpStop(int dir)
        {
            var stops = Stops();
            var cur = CurrentStop(stops);
            var next = Math.Clamp(cur + dir, 0, stops.Count - 1);
            // From between stops, ⏭ goes to the next one after where you are.
            if (dir > 0 && stops[cur].At > _pos.Anchor + TimeSpan.FromMinutes(1)) next = cur;
            _pos = GuideFocus.ToTime(_g!, _pos, stops[next].At);
            Draw();
        }

        private int CurrentStop(IReadOnlyList<(string Label, DateTimeOffset At)> stops)
        {
            var i = 0;
            for (var k = 0; k < stops.Count; k++) if (stops[k].At <= _pos.Anchor + TimeSpan.FromMinutes(1)) i = k;
            return i;
        }

        // ------------------------------------------------------------------ drawing
        private void Draw()
        {
            if (_g == null || _model == null) return;
            var now = _ctx.Now();
            _g = _g with { Now = now };
            var p = Lit;
            var ch = _model.Channels[_pos.Ch].Channel;
            _grid.Draw(_pos, p, now, _focused && !_actions.IsOpen);
            _header.Describe(ch, p, now, Hints(p, now));
            var stops = Stops();
            _header.SetStrip(stops.Select(s => s.Label).ToList(), CurrentStop(stops));

            var cur = _ctx.Playback.Current;
            var playing = cur != null;
            _header.SetHole(playing);
            var onNow = GuideFocus.At(_model.Channels[_pos.Ch].Schedule, now);
            _header.Preview(cur == null ? ch.Name : cur.Channel?.Name ?? cur.Title, cur?.Kind == "live", playing, ch, onNow);
        }

        private static IReadOnlyList<(string, string)> Hints(Programme p, DateTimeOffset now)
        {
            var hints = new List<(string, string)>();
            if (p.NoInfo || p.IsOnAt(now)) hints.Add(("OK", "Watch"));
            else if (p.Start > now) hints.Add(("OK", "When it's on"));
            hints.Add(("Hold OK", "More"));
            hints.Add(("Pg", "Page channels"));
            hints.Add(("0–9", "Channel"));
            return hints;
        }
    }
}
