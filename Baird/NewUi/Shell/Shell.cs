using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Baird.NewUi.Core;
using Baird.NewUi.Screens;
using Baird.NewUi.Theme;

namespace Baird.NewUi.Shell
{
    public sealed record ShellDeps(
        Control Video,
        Navigator Nav,
        Func<Place, IScreen> CreatePlace,
        Func<PageRef, IScreen> CreatePage,
        Func<OverlayRef, IScreen> CreateOverlay,
        Func<UiAction, bool, bool> VideoAction,
        Func<string> BackToVideoLabel);

    /// <summary>
    /// The new UI's layer stack and global keys (ported from the prototype's app.js):
    /// video (persistent, never re-parented) · places (tabs) · pages · tab bar · back chip · overlays · toast.
    /// Places crossfade from the side you moved towards; pages rise in and drop out; overlays animate out as well as in;
    /// what's beneath stays on screen until what covers it has arrived.
    /// </summary>
    public sealed class Shell : Grid
    {
        public static readonly Place[] Tabs = { Place.Home, Place.LiveTv, Place.Search, Place.Settings };
        private static readonly Dictionary<Place, string> TabLabels = new() { [Place.Home] = "Home", [Place.LiveTv] = "Live TV", [Place.Search] = "Search", [Place.Settings] = "Settings" };

        private readonly ShellDeps _d;
        public Panel Stage { get; } = new() { Width = 1920, Height = 1080, ClipToBounds = true };
        private readonly Panel _placeHost = new();
        private readonly Panel _pageHost = new();
        private readonly Panel _overlayHost = new();
        private readonly StackPanel _tabBar = new() { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 34, 0, 0) };
        private readonly Border _backChip;
        private readonly TextBlock _backChipText = new() { FontSize = 24, FontWeight = FontWeight.SemiBold, Foreground = T.Text2, VerticalAlignment = VerticalAlignment.Center };
        private readonly Border _toast;
        private readonly TextBlock _toastText = new() { FontSize = 26, FontWeight = FontWeight.SemiBold, Foreground = T.Text, TextWrapping = TextWrapping.Wrap, MaxWidth = 1300 };
        private readonly TextBlock _clock = new() { FontSize = 28, FontWeight = FontWeight.SemiBold, Foreground = T.Text2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 46, T.SafeX, 0) };

        private readonly Dictionary<Place, IScreen> _places = new();
        private readonly Dictionary<PageRef, IScreen> _pages = new(ReferenceEqualityComparer.Instance);
        private IScreen? _overlay;
        private OverlayRef? _shownOverlayRef;
        private Place? _shownPlace;
        private PageRef? _shownPage;
        private bool _placeWasHidden;
        private IDisposable? _toastTimer, _chipTimer;
        private bool _chipFlashing;

        /// <summary>Is the tab bar lit (rather than the place's content)?</summary>
        public bool TabFocus { get; private set; }

        public Shell(ShellDeps deps)
        {
            _d = deps;
            Background = Brushes.Transparent;
            SetValue(Avalonia.Controls.Documents.TextElement.FontFamilyProperty, T.Font);

            foreach (var p in Tabs)
                _tabBar.Children.Add(new Border { CornerRadius = new CornerRadius(999), Padding = new Thickness(26, 10), Child = new TextBlock { Text = TabLabels[p], FontSize = 27, FontWeight = FontWeight.SemiBold }, Tag = p });
            Motion.Animate(_tabBar, Motion.Med);
            foreach (var tab in _tabBar.Children.OfType<Border>()) { tab.RenderTransformOrigin = RelativePoint.Center; Motion.Animate(tab, T.Fast); }
            Motion.Animate(_placeHost, T.Fast);

            _backChip = new Border
            {
                Background = T.Glass,
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(16, 10, 22, 10),
                Opacity = 0,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(T.SafeX, 44, 0, 0),
                IsHitTestVisible = false,
                Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { new TextBlock { Text = "‹", FontSize = 34, Foreground = T.Text, VerticalAlignment = VerticalAlignment.Center }, _backChipText } },
            };
            Motion.Animate(_backChip, T.Fast);
            _toast = new Border
            {
                Background = T.Glass,
                CornerRadius = new CornerRadius(20),
                Padding = new Thickness(30, 18),
                Opacity = 0,
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 60, 0, 0),
                Child = _toastText,
            };
            Motion.Animate(_toast, Motion.Med);
            _toast.RenderTransform = Motion_Translate(-20);
            _backChip.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("translate(-16px, 0px)");

            Stage.Children.Add(_placeHost);
            Stage.Children.Add(_pageHost);
            Stage.Children.Add(_tabBar);
            Stage.Children.Add(_backChip);
            Stage.Children.Add(_clock);
            Stage.Children.Add(_overlayHost);
            Stage.Children.Add(_toast);

            // The video fills the window outside the scaled stage (it renders at native resolution).
            Children.Add(deps.Video);
            Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = Stage });

            deps.Nav.Changed += Render;
            var clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            clock.Tick += (_, _) => _clock.Text = DateTime.Now.ToString("HH:mm");
            clock.Start();
            _clock.Text = DateTime.Now.ToString("HH:mm");
            Render();
        }

        public IScreen PlaceScreen(Place p)
        {
            if (!_places.TryGetValue(p, out var s)) _places[p] = s = _d.CreatePlace(p);
            return s;
        }

        public IScreen? ActiveScreen
        {
            get
            {
                var st = _d.Nav.State;
                if (_overlay != null && st.Overlay != null) return _overlay;
                if (st.Pages.Count > 0 && _pages.TryGetValue(st.Pages[^1], out var page)) return page;
                if (st.Video != null && st.AtVideo) return null;
                return PlaceScreen(st.Place);
            }
        }

        public void Toast(string text)
        {
            _toastText.Text = text;
            _toast.Opacity = 1;
            _toast.RenderTransform = Motion.None;   // slides down into place…
            _toastTimer?.Dispose();
            _toastTimer = DispatcherTimer.RunOnce(() => { _toast.Opacity = 0; _toast.RenderTransform = Motion_Translate(-20); }, TimeSpan.FromSeconds(3.2));
        }

        /// <summary>Entering the video: briefly say where Back will go.</summary>
        public void FlashBackChip(string to)
        {
            _backChipText.Text = $"Back to {to}";
            _backChip.Opacity = 1;
            _backChip.RenderTransform = Motion.None;
            _chipFlashing = true;
            _chipTimer?.Dispose();
            _chipTimer = DispatcherTimer.RunOnce(() => { _chipFlashing = false; _backChip.Opacity = 0; Render(); }, TimeSpan.FromSeconds(2.6));
        }

        // ------------------------------------------------------------------ rendering the stack
        private void Render()
        {
            var st = _d.Nav.State;
            var placeVisible = st.Pages.Count == 0 && !(st.Video != null && st.AtVideo);

            // Places: keep the one we're leaving until the new one has come in (nothing flashes through).
            if (placeVisible && _shownPlace != st.Place)
            {
                var screen = PlaceScreen(st.Place);
                var dir = _shownPlace is { } prev ? Math.Sign(Array.IndexOf(Tabs, st.Place) - Array.IndexOf(Tabs, prev)) : 0;
                foreach (var old in _placeHost.Children.ToList())
                    if (old != screen.View) DispatcherTimer.RunOnce(() => _placeHost.Children.Remove(old), Motion.Med + TimeSpan.FromMilliseconds(40));
                if (!_placeHost.Children.Contains(screen.View)) _placeHost.Children.Add(screen.View);
                Motion.Enter(screen.View, dir * 70, 0);
                screen.OnShow();
                if (TabFocus) screen.OnFocusOut(); else screen.OnFocusIn();
                _shownPlace = st.Place;
            }
            else if (placeVisible && _placeWasHidden) PlaceScreen(st.Place).OnShow();
            _placeWasHidden = !placeVisible;
            // Hide the place only once whatever covers it has arrived.
            if (placeVisible) _placeHost.IsVisible = true;
            else DispatcherTimer.RunOnce(() => { var s2 = _d.Nav.State; if (s2.Pages.Count > 0 || (s2.Video != null && s2.AtVideo)) _placeHost.IsVisible = false; }, Motion.Med + TimeSpan.FromMilliseconds(40));

            // Pages.
            var page = st.Pages.Count > 0 ? st.Pages[^1] : null;
            if (!ReferenceEquals(page, _shownPage))
            {
                var leaving = _shownPage != null && _pages.TryGetValue(_shownPage, out var ls) ? ls : null;
                var popped = _shownPage != null && !st.Pages.Contains(_shownPage) && !st.OriginPages.Contains(_shownPage);
                if (page != null)
                {
                    if (!_pages.TryGetValue(page, out var ps)) _pages[page] = ps = _d.CreatePage(page);
                    if (!_pageHost.Children.Contains(ps.View)) _pageHost.Children.Insert(0, ps.View);
                    Motion.Enter(ps.View, 0, popped ? 0 : 60);
                    ps.OnShow();
                }
                if (leaving != null)
                {
                    var v = leaving.View;
                    Motion.Exit(v, 0, popped ? 14 : 0, () => { if (!ReferenceEquals(_pages.GetValueOrDefault(_shownPage!), leaving)) _pageHost.Children.Remove(v); });
                    if (popped) { leaving.Destroy(); _pages.Remove(_shownPage!); }
                }
                _shownPage = page;
            }

            // Overlays: the old one animates out.
            if (!ReferenceEquals(st.Overlay, _shownOverlayRef))
            {
                if (_overlay != null)
                {
                    var old = _overlay; var v = old.View;
                    var (ox, oy) = old.EnterFrom;
                    old.Destroy();
                    // It leaves the way it came (a short way: the next one is already arriving).
                    Motion.Exit(v, ox * 0.5, oy * 0.35, () => _overlayHost.Children.Remove(v));
                }
                _overlay = st.Overlay != null ? _d.CreateOverlay(st.Overlay) : null;
                if (_overlay != null) { _overlayHost.Children.Add(_overlay.View); var (ex, ey) = _overlay.EnterFrom; Motion.Enter(_overlay.View, ex, ey); _overlay.OnShow(); }
                _shownOverlayRef = st.Overlay;
            }

            // Tab bar: only on places; lit when the bar has focus.
            _tabBar.Opacity = placeVisible ? 1 : 0;
            _tabBar.RenderTransform = placeVisible ? Motion.None : Motion_Translate(-24);
            foreach (var tab in _tabBar.Children.OfType<Border>())
            {
                var p = (Place)tab.Tag!;
                var current = p == st.Place;
                var lit = TabFocus && current && st.Overlay == null;
                tab.Background = lit ? T.FocusFill : current ? T.Pill : Brushes.Transparent;
                tab.RenderTransform = lit ? Motion.Scale(1.08) : Motion.None;
                ((TextBlock)tab.Child!).Foreground = lit ? T.FocusInk : current ? T.Text : T.Text2;
                tab.Classes.Set("focused", lit);
            }

            // Where Back goes, on stacked pages.
            if (!_chipFlashing)
            {
                var below = st.Pages.Count >= 2 ? st.Pages[^2].Label : null;
                string? backTo = page != null && st.Overlay == null ? below ?? (st.Video != null && st.AtVideo ? _d.BackToVideoLabel() : TabLabels[st.Place]) : null;
                _backChip.Opacity = backTo != null ? 1 : 0;
                _backChip.RenderTransform = backTo != null ? Motion.None : Avalonia.Media.Transformation.TransformOperations.Parse("translate(-16px, 0px)");
                if (backTo != null) _backChipText.Text = $"Back to {backTo}";
            }
            _clock.IsVisible = !(st.Video != null && st.AtVideo && page == null);
            // With the tab bar lit, the place's content steps back.
            _placeHost.Opacity = TabFocus && placeVisible ? 0.75 : 1;
        }

        private static ITransform Motion_Translate(double y) => Avalonia.Media.Transformation.TransformOperations.Parse($"translate(0px, {y}px)");

        // ------------------------------------------------------------------ global keys
        public void OnAction(UiAction a, bool repeat)
        {
            var nav = _d.Nav;
            var st = nav.State;
            switch (a)
            {
                case UiAction.Home:
                    nav.Home(); TabFocus = false; PlaceScreen(Place.Home).OnFocusIn();
                    if (PlaceScreen(Place.Home) is HomeScreen home) home.Reset(); // the Home key: back to the first card
                    Render(); return;
                case UiAction.Guide:
                    // The guide's own key inside the guide; over the video, the guide opens as a page (Back returns to
                    // the picture); elsewhere, the Live TV place.
                    if (st.Pages.Count > 0 && st.Pages[^1].Name == GuidePage && _pages.TryGetValue(st.Pages[^1], out var gp)) { nav.CloseOverlay(); gp.OnAction(a, repeat); return; }
                    if (st.Video != null && st.AtVideo && st.Pages.Count == 0) { nav.OpenPage(new PageRef(GuidePage)); return; }
                    ShowPlace(Place.LiveTv, st.Place == Place.LiveTv && st.Pages.Count == 0 && !st.AtVideo ? a : null); return;
                case UiAction.Voice:
                    ShowPlace(Place.Search, a); return;
            }

            // The tab bar: moving across it switches the place straight away; ▾ / OK enter the content.
            var onPlace = st.Overlay == null && st.Pages.Count == 0 && !(st.Video != null && st.AtVideo);
            if (onPlace && TabFocus)
            {
                var i = Array.IndexOf(Tabs, st.Place);
                if (a == UiAction.Left && i > 0) { nav.SetPlace(Tabs[i - 1]); return; }
                if (a == UiAction.Right && i < Tabs.Length - 1) { nav.SetPlace(Tabs[i + 1]); return; }
                if (a is UiAction.Down or UiAction.Ok) { TabFocus = false; PlaceScreen(st.Place).OnFocusIn(); Render(); return; }
                if (a == UiAction.Back) { if (st.Place != Place.Home) nav.SetPlace(Place.Home); return; }
                if (a is UiAction.Left or UiAction.Right or UiAction.Up) return;
            }

            var screen = ActiveScreen;
            var handled = screen != null ? screen.OnAction(a, repeat) : _d.VideoAction(a, repeat);
            if (handled) return;
            // Over the video, Pg±, digits and ⏮/⏭ keep their meaning whatever overlay is up.
            if (st.Overlay != null && st.Video != null && st.AtVideo && st.Pages.Count == 0
                && (a is UiAction.PageUp or UiAction.PageDown or UiAction.Prev or UiAction.Next || InputMap.DigitOf(a) != null)
                && _d.VideoAction(a, repeat)) return;
            if (onPlace && (a == UiAction.Up || a == UiAction.Back))
            {
                // ▴ off the top of a place's content, or Back from inside it, lights its tab.
                TabFocus = true; PlaceScreen(st.Place).OnFocusOut(); Render(); return;
            }
            if (a == UiAction.Back) nav.Back();
            else if (a == UiAction.PlayPause) _d.VideoAction(a, repeat);
        }

        /// <summary>The page name the guide uses when it opens over the video.</summary>
        public const string GuidePage = "guide";

        public void GoPlace(Place p)
        {
            _d.Nav.GoPlace(p); // one change: nothing (Home) flashes in between
            TabFocus = false;
            PlaceScreen(p).OnFocusIn();
            Render();
        }

        /// <summary>
        /// Show a place's content: if it's already the bare place, only close any overlay and take focus from the tab bar
        /// (its focus stays where it was); otherwise go there. Then hand it <paramref name="key"/>, if any (≡, voice).
        /// </summary>
        public void ShowPlace(Place p, UiAction? key = null)
        {
            var st = _d.Nav.State;
            if (st.Place == p && st.Pages.Count == 0 && !st.AtVideo)
            {
                _d.Nav.CloseOverlay();
                if (TabFocus) { TabFocus = false; PlaceScreen(p).OnFocusIn(); }
                Render();
            }
            else GoPlace(p);
            if (key is { } k) PlaceScreen(p).OnAction(k, false);
        }
    }
}
