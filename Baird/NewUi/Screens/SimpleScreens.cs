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
using Baird.NewUi.Core;
using Baird.NewUi.Model;
using Baird.NewUi.Shell;
using Baird.NewUi.Theme;
using Baird.Services;

namespace Baird.NewUi.Screens
{
    /// <summary>A lit-able list row (Settings, the On-now list).</summary>
    public sealed class ListRow : Border
    {
        private readonly TextBlock _label, _value;
        private readonly Avalonia.Controls.Shapes.Ellipse _dot = new() { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        public string LabelText => _label.Text ?? "";
        public string ValueText => _value.Text ?? "";
        public ListRow(string label, string? value)
        {
            Height = 80; CornerRadius = new CornerRadius(18); Padding = new Thickness(28, 0);
            _label = new TextBlock { Text = label, FontSize = 28, FontWeight = FontWeight.SemiBold, Foreground = T.Text, VerticalAlignment = VerticalAlignment.Center };
            _value = new TextBlock { Text = value ?? "", FontSize = 26, Foreground = T.Text2, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            Child = new Grid { Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { _dot, _label } }, _value } };
            Motion.Animate(this, T.Fast);
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        }
        public string Value { set => _value.Text = value; }

        /// <summary>A status dot before the label (Settings' sources).</summary>
        public SourceHealth? Dot
        {
            set
            {
                _dot.IsVisible = value != null;
                _dot.Fill = value switch
                {
                    SourceHealth.Ok => new SolidColorBrush(Color.Parse("#34C759")),
                    SourceHealth.Warning => new SolidColorBrush(Color.Parse("#FFCC00")),
                    SourceHealth.Down => T.Live,
                    _ => T.Text3,
                };
            }
        }
        public bool Lit
        {
            set
            {
                Classes.Set("focused", value);
                Background = value ? T.FocusFill : Brushes.Transparent;
                _label.Foreground = value ? T.FocusInk : T.Text;
                _value.Foreground = value ? new SolidColorBrush(Color.Parse("#990A0A0C")) : T.Text2;
                RenderTransform = value ? TransformOperations.Parse("scale(1.02)") : TransformOperations.Parse("none");
                BoxShadow = value ? T.FocusGlow : default;
            }
        }
    }

    /// <summary>A vertical list with a title on the left: Settings, and the Search placeholder.</summary>
    public abstract class ListScreen : IScreen
    {
        protected readonly Panel Root = new() { Width = 1920, Height = 1080, Background = new SolidColorBrush(Color.Parse("#F0000000")) };
        protected readonly StackPanel List = new() { Spacing = 6, Width = 1060, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(760, 150, 0, 0) };
        protected List<ListRow> Rows = new();
        protected int Index;
        private bool _lit = true;
        public Control View => Root;
        public Control? LitElement => _lit && Rows.Count > 0 ? Rows[Index] : null;

        protected ListScreen(string title, string? note)
        {
            var side = new StackPanel { Width = 600, Margin = new Thickness(T.SafeX, 320, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Spacing = 20 };
            side.Children.Add(new TextBlock { Text = title, FontSize = 72, FontWeight = FontWeight.Bold, Foreground = T.Text });
            if (note != null) side.Children.Add(new TextBlock { Text = note, FontSize = 26, Foreground = T.Text2, TextWrapping = TextWrapping.Wrap });
            Root.Children.Add(side);
            Root.Children.Add(List);
            Motion.Animate(List, Motion.Med);
        }

        private const double RowH = 80, HeadingH = 64;
        private readonly Dictionary<ListRow, string> _groups = new();

        /// <summary>Rows, with a heading before the rows that start a group (Settings).</summary>
        protected void SetRows(IEnumerable<ListRow> rows, IReadOnlyDictionary<ListRow, string>? groups = null)
        {
            Rows = rows.ToList(); List.Children.Clear();
            _groups.Clear();
            if (groups != null) foreach (var kv in groups) _groups[kv.Key] = kv.Value;
            foreach (var r in Rows)
            {
                if (_groups.TryGetValue(r, out var heading))
                    List.Children.Add(new TextBlock { Text = heading, Height = HeadingH, FontSize = 24, FontWeight = FontWeight.SemiBold, Foreground = T.Text3, Padding = new Thickness(28, 26, 0, 0) });
                List.Children.Add(r);
            }
            Index = Math.Clamp(Index, 0, Math.Max(0, Rows.Count - 1));
            Draw();
        }

        protected void Draw()
        {
            for (var i = 0; i < Rows.Count; i++) Rows[i].Lit = _lit && i == Index;
            // Keep the lit row (and its heading) on screen: scroll by its real position in the list.
            var y = 0.0;
            for (var i = 0; i < Rows.Count && i < Index; i++) y += RowH + List.Spacing + (_groups.ContainsKey(Rows[i]) ? HeadingH + List.Spacing : 0);
            if (Rows.Count > 0 && _groups.ContainsKey(Rows[Index])) y += HeadingH + List.Spacing;
            const double view = T.SafeBottom - 150 - 30;
            List.RenderTransform = TransformOperations.Parse($"translate(0px, {-Math.Max(0, y + RowH - view)}px)");
        }

        public virtual bool OnAction(UiAction a, bool repeat)
        {
            if (a == UiAction.Up && Index == 0) return false;
            if (a is UiAction.Up or UiAction.Down && Rows.Count > 0) { Index = Math.Clamp(Index + (a == UiAction.Up ? -1 : 1), 0, Rows.Count - 1); Draw(); return true; }
            return false;
        }
        public virtual void OnShow() { }
        public void OnFocusIn() { _lit = true; Draw(); }
        public void OnFocusOut() { _lit = false; Draw(); }
    }

    /// <summary>New-mode Settings: Interface (Classic / New, restarts) and Interface size. ◂ ▸ change values.</summary>
    public sealed class SettingsScreen : ListScreen
    {
        private readonly Func<UiMode> _getMode;
        private readonly Action<UiMode> _setModeAndRestart;
        private readonly ListRow _interface, _size;
        private double _scale;
        private readonly Action<double> _setScale;
        private readonly Func<Model.GuideLayout>? _getLayout;
        private readonly Action<Model.GuideLayout>? _setLayout;
        private readonly ListRow? _layout;

        public SettingsScreen(Func<UiMode> getMode, Action<UiMode> setModeAndRestart, double scale, Action<double> setScale,
            Func<Model.GuideLayout>? getGuideLayout = null, Action<Model.GuideLayout>? setGuideLayout = null, UpdateService? updates = null,
            SettingsOptions? options = null) : base("Settings", null)
        {
            _getMode = getMode; _setModeAndRestart = setModeAndRestart; _scale = scale; _setScale = setScale;
            _getLayout = getGuideLayout; _setLayout = setGuideLayout; _options = options;
            _interface = new ListRow("Interface", "New  ·  ◂ ▸ to switch to Classic");
            _size = new ListRow("Interface size", $"‹  {_scale * 100:0}%  ›");
            var rows = new List<ListRow> { _interface, _size };
            var groups = new Dictionary<ListRow, string> { [_interface] = "Picture and sound" };
            if (_getLayout != null && _setLayout != null) rows.Add(_layout = new ListRow("Guide layout", LayoutText(_getLayout())));
            if (options != null)
            {
                rows.Add(_subtitles = new ListRow("Subtitles by default", OnOff(options.GetSubtitles())));
                rows.Add(_autoplay = new ListRow("Play the next episode automatically", OnOff(options.GetAutoplay())));
            }
            _updates = updates;
            if (updates != null) { rows.Add(_update = new ListRow("Software update", $"{updates.Current} · checking…")); _ = CheckAsync(); }
            if (options?.Sources != null)
            {
                foreach (var name in options.SourceNames)
                {
                    var row = new ListRow(name, "Checking…") { Dot = SourceHealth.Off };
                    if (_sources.Count == 0) groups[row] = "Sources";
                    _sources[name] = row;
                    rows.Add(row);
                }
            }
            if (options?.About != null)
            {
                rows.Add(_about = new ListRow("About Baird", options.About));
                groups[_update ?? _about] = "About";
            }
            else if (_update != null) groups[_update] = "About";
            SetRows(rows, groups);
            if (options?.Sources != null) Loaded = RefreshSourcesAsync();
        }

        private readonly SettingsOptions? _options;
        private readonly ListRow? _subtitles, _autoplay, _about;
        private readonly Dictionary<string, ListRow> _sources = new();
        public Task Loaded { get; private set; } = Task.CompletedTask;
        public IReadOnlyList<(string Label, string Value)> RowTexts => Rows.Select(r => (r.LabelText, r.ValueText)).ToList();
        private static string OnOff(bool on) => on ? "‹  On  ›" : "‹  Off  ›";

        private async Task RefreshSourcesAsync()
        {
            try
            {
                var statuses = await _options!.Sources!();
                void Apply()
                {
                    foreach (var st in statuses)
                        if (_sources.TryGetValue(st.Name, out var row)) { row.Value = st.Text; row.Dot = st.Health; }
                }
                if (Dispatcher.UIThread.CheckAccess()) Apply(); else Dispatcher.UIThread.Post(Apply);
            }
            catch (Exception ex) { Console.WriteLine($"[Settings] Source status failed: {ex.Message}"); }
        }

        private readonly UpdateService? _updates;
        private readonly ListRow? _update;
        private string _updateText = "";
        private string? _latest;
        private bool _updating, _checking;
        public string UpdateText => _updateText;

        private void SetUpdate(string text) { _updateText = text; if (_update != null) _update.Value = text; }

        private async System.Threading.Tasks.Task CheckAsync()
        {
            if (_updates == null || _checking) return;
            _checking = true;
            SetUpdate($"{_updates.Current} · checking…");
            _latest = await _updates.LatestAsync();
            _checking = false;
            if (_updating) return;
            if (_latest == null) SetUpdate($"{_updates.Current} · couldn't check for updates · OK to try again");
            else if (UpdateService.IsNewer(_latest, _updates.Current)) SetUpdate($"{_updates.Current} · {_latest} available · OK to update");
            else SetUpdate($"{_updates.Current} · up to date · OK to check again");
        }

        private static string LayoutText(Model.GuideLayout l) => l == Model.GuideLayout.Columns ? "‹  Channels across  ›" : "‹  Channels down  ›";

        public override void OnShow()
        {
            if (_layout != null) _layout.Value = LayoutText(_getLayout!()); // the guide's strip may have switched it
            if (_options?.Sources != null) Loaded = RefreshSourcesAsync();
        }

        public override bool OnAction(UiAction a, bool repeat)
        {
            if (_update != null && Index < Rows.Count && Rows[Index] == _update && a == UiAction.Ok)
            {
                if (_updating) return true;
                if (_updates != null && UpdateService.IsNewer(_latest, _updates.Current))
                {
                    _updating = true;
                    SetUpdate($"Updating to {_latest}… Baird restarts by itself when it's ready");
                    _updates.StartUpdate();
                }
                else _ = CheckAsync();
                return true;
            }
            var lit = Index < Rows.Count ? Rows[Index] : null;
            if (lit != null && (lit == _subtitles || lit == _autoplay) && a is UiAction.Left or UiAction.Right or UiAction.Ok)
            {
                if (lit == _subtitles) { var on = !_options!.GetSubtitles(); _options.SetSubtitles(on); lit.Value = OnOff(on); }
                else { var on = !_options!.GetAutoplay(); _options.SetAutoplay(on); lit.Value = OnOff(on); }
                return true;
            }
            if (Index == 0 && a is UiAction.Left or UiAction.Right or UiAction.Ok)
            {
                _interface.Value = "Switching to Classic… Baird restarts";
                _setModeAndRestart(_getMode() == UiMode.New ? UiMode.Classic : UiMode.New);
                return true;
            }
            if (Index == 1 && a is UiAction.Left or UiAction.Right)
            {
                _scale = Math.Clamp(Math.Round(_scale + (a == UiAction.Left ? -0.1 : 0.1), 1), 0.8, 1.3);
                _size.Value = $"‹  {_scale * 100:0}%  ›";
                _setScale(_scale);
                return true;
            }
            if (lit != null && lit == _layout && a is UiAction.Left or UiAction.Right or UiAction.Ok)
            {
                var next = _getLayout!() == Model.GuideLayout.Rows ? Model.GuideLayout.Columns : Model.GuideLayout.Rows;
                _setLayout!(next);
                _layout.Value = LayoutText(next);
                return true;
            }
            return base.OnAction(a, repeat);
        }
    }

    /// <summary>
    /// Settings that need the app around them: subtitles and autoplay (read and saved through delegates, so tests never
    /// write ~/.baird/config.ini), the sources' status rows (refreshed each time Settings is shown), and About.
    /// </summary>
    public sealed record SettingsOptions(Func<bool> GetSubtitles, Action<bool> SetSubtitles, Func<bool> GetAutoplay, Action<bool> SetAutoplay,
        Func<Task<IReadOnlyList<SourceStatus>>>? Sources = null, IReadOnlyList<string>? SourceRows = null, string? About = null)
    {
        public static readonly string[] DefaultSourceRows = { "Freesat (TvHeadend)", "Jellyfin", "Seerr requests", "Netflix", "Disney+" };
        public IReadOnlyList<string> SourceNames => SourceRows ?? DefaultSourceRows;
    }

    /// <summary>A page that isn't built yet (none are reachable in phase 1; a safe fallback).</summary>
    public sealed class EmptyScreen : IScreen
    {
        public Control View { get; } = new Border { Width = 1920, Height = 1080, Background = Brushes.Black };
        public bool OnAction(UiAction a, bool repeat) => a != UiAction.Back && a != UiAction.Up;
    }
}
