using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Baird.Mpv;
using Baird.NewUi.Adapters;
using Baird.NewUi.Core;
using Baird.NewUi.Lifecycle;
using Baird.NewUi.Playback;
using Baird.NewUi.Screens;
using Baird.Services;
using Microsoft.Extensions.Configuration;

namespace Baird.NewUi
{
    /// <summary>
    /// The new UI's composition root: builds the services, the one video surface, playback, navigation and the shell,
    /// and turns remote keys into actions. Chosen at startup when Baird:Ui = new.
    /// </summary>
    public class NewShellView : UserControl
    {
        private readonly InputMap _input;
        private readonly HoldDetector _hold = new();
        private readonly DispatcherTimer _holdTick = new() { Interval = TimeSpan.FromMilliseconds(50) };
        private readonly Shell.Shell _shell;
        private readonly NewUiContext _ctx;
        private Key? _lastKey;
        private readonly TvLifecycle _life;
        private Intro? _intro = new();

        public NewShellView(IConfiguration config)
        {
            Background = Brushes.Transparent;
            var voiceKey = int.TryParse(config["VOICE_COMMAND_KEY"], out var vk) && vk != 0 ? (Key)vk : Key.None;
            _input = new InputMap(voiceKey);

            var services = new LegacyServices(config);
            var mpv = new MpvPlayer(services.MpvOverrides);
            var surface = new VideoSurface(mpv);
            surface.Renderer += r =>
            {
                Shell.Motion.Lite = r.Contains("V3D", StringComparison.OrdinalIgnoreCase) || r.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase) || r.Contains("SwiftShader", StringComparison.OrdinalIgnoreCase);
                Console.WriteLine($"[NewUi] Motion: {(Shell.Motion.Lite ? "lite" : "full")}");
            };

            var nav = new Navigator();
            var history = new HistoryWriter(services.Data);
            var timers = new DispatcherTimers();
            var router = new EngineRouter(mpv, services.BrowserEngines);
            var playback = new PlaybackController(router, a => Dispatcher.UIThread.Post(a), timers, () => DateTimeOffset.Now,
                (id, pos, dur) => _ = history.SaveAsync(id, pos, dur), mpv.ConfigureForSource);
            var home = new HomeData(services.Data, services.Epg, services.Seerr);
            _ctx = new NewUiContext(nav, playback, home, () => DateTimeOffset.Now) { Timers = timers };
            _ctx.Search = new CatalogueSearch(services.Data, services.Epg, services.Seerr, new SearchHistoryService());
            _ctx.Shows = new ShowData(services.Data, services.Seerr, _ctx.Search);
            var guideData = new GuideData(home, services.Epg);
            _ctx.Reminders = new Reminders(Reminders.DefaultPath, () => DateTimeOffset.Now);
            _ctx.SetGuideLayout(string.Equals(config["Baird:GuideLayout"], "columns", StringComparison.OrdinalIgnoreCase) ? Model.GuideLayout.Columns : Model.GuideLayout.Rows, save: false);
            _ctx.SaveGuideLayout = l => IniSettingWriter.Save(IniSettingWriter.DefaultPath, "Baird", "GuideLayout", l == Model.GuideLayout.Columns ? "columns" : "rows");
            var videoActions = new VideoActions(_ctx, timers);
            var ident = new TuneIdent();
            _ctx.CycleAudio = () => router.ActiveBrowser == null ? mpv.CycleAudio() : null;
            _ctx.TryActivatePrompt = () => router.PromptText != null && router.ActivatePrompt();
            _ctx.FailureAction = () => router.FailureAction;
            _ctx.RunFailureAction = () => router.RunFailureActionAsync();
            CodeEntry? codeEntry = null;

            var scale = double.TryParse(config["Baird:NewUiSize"], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 1.0;
            _shell = new Shell.Shell(new Shell.ShellDeps(
                surface, nav,
                CreatePlace: p => p switch
                {
                    Place.Home => new HomeScreen(_ctx),
                    Place.LiveTv => new GuideScreen(_ctx, guideData.LoadAsync, timers),
                    Place.Search => new SearchScreen(_ctx),
                    _ => new SettingsScreen(() => UiMode.New, mode => { UiModes.Save(mode); AppRestarter.ForThisProcess().Restart(); }, scale, v =>
                    {
                        ApplyScale(v);
                        IniSettingWriter.Save(IniSettingWriter.DefaultPath, "Baird", "NewUiSize", v.ToString("0.0", CultureInfo.InvariantCulture));
                    }, () => _ctx.GuideLayout, l => _ctx.SetGuideLayout(l)),
                },
                CreatePage: p => p.Name == "show" && p.Params is Model.CatalogueTitle t ? new ShowPage(_ctx, t) : new EmptyScreen(),
                CreateOverlay: o => o.Name switch
                {
                    "info" => new InfoBar(_ctx),
                    "channels" => new ChannelList(_ctx, timers),
                    "actions" => new ActionsPanel(_ctx, () => GuideScreen.OpenSchedule(_shell!, playback.Current?.Channel?.Id, DateTimeOffset.Now)),
                    "reminder" when o.Params is Model.Reminder r => new ReminderCard(_ctx, r),
                    "code" => codeEntry = new CodeEntry(() => router.CodeRequest, router.SubmitCodeAsync, () => { playback.Stop(); nav.Stop(); }),
                    _ when o.Params is ChoiceList list => new ChoiceSheet(_ctx, list),
                    _ => new EmptyScreen(),
                },
                VideoAction: videoActions.OnAction,
                BackToVideoLabel: () => playback.Current?.Title ?? "what's playing"));
            _ctx.Toast = _shell.Toast;
            _ctx.FlashBackChip = _shell.FlashBackChip;
            _shell.Stage.Children.Insert(0, ident); // over the picture, under everything else
            var promptText = new TextBlock { FontSize = 28, FontWeight = FontWeight.SemiBold, Foreground = Baird.NewUi.Theme.T.FocusInk };
            var prompt = new Border
            {
                Background = Baird.NewUi.Theme.T.FocusFill,
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(30, 14),
                Opacity = 0,
                IsHitTestVisible = false,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 96, 120),
                Child = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 14, Children = { promptText, new TextBlock { Text = "OK", FontSize = 22, Foreground = Baird.NewUi.Theme.T.FocusInk, Opacity = 0.6, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center } } },
            };
            Shell.Motion.Animate(prompt, Baird.NewUi.Theme.T.Fast);
            _shell.Stage.Children.Insert(1, prompt);
            // Live TV, last 5 minutes of a programme: a corner card with what's next (OK keeps watching; ▴ ▾ find something else).
            var upNextText = new TextBlock { FontSize = 24, Foreground = Baird.NewUi.Theme.T.Text, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 };
            var upNextCard = new Border
            {
                Background = Baird.NewUi.Theme.T.Glass,
                CornerRadius = new CornerRadius(20),
                Padding = new Thickness(28, 20),
                Opacity = 0,
                IsHitTestVisible = false,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Margin = new Thickness(0, 64, 96, 0),
                Child = upNextText,
            };
            Shell.Motion.Animate(upNextCard, Baird.NewUi.Theme.T.Med);
            _shell.Stage.Children.Insert(2, upNextCard);
            var liveUpNext = new LiveUpNext();
            var nearEnd = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            nearEnd.Tick += async (_, _) =>
            {
                // Keep "what's on" current as programmes change while a channel plays.
                if (playback.Current is { Kind: "live", Channel: { } live } && (_ctx.Programme == null || DateTimeOffset.Now >= _ctx.Programme.End))
                {
                    var fresh = await home.CurrentProgrammeAsync(live.Id);
                    if (playback.Current?.Channel?.Id == live.Id) _ctx.Programme = fresh;
                }
                var prog = _ctx.Programme;
                if (playback.Current is not { Kind: "live", Channel: { } ch } || !nav.State.AtVideo || nav.State.Overlay != null || !liveUpNext.ShouldShow(prog, DateTimeOffset.Now))
                {
                    if (upNextCard.Opacity > 0 && (prog == null || DateTimeOffset.Now >= prog.End || !nav.State.AtVideo)) upNextCard.Opacity = 0;
                    return;
                }
                if (upNextCard.Opacity > 0) return;
                var next = await home.NextProgrammeAsync(ch.Id, DateTimeOffset.Now);
                if (next == null) return;
                upNextText.Text = $"Up next on {ch.Name}\n{next.Start.LocalDateTime:HH:mm}  {next.Title}\nOK to stay · ▴ ▾ for something else";
                upNextCard.Opacity = 1;
                liveUpNext.Dismiss(prog);
                DispatcherTimer.RunOnce(() => upNextCard.Opacity = 0, TimeSpan.FromSeconds(20));
            };
            nearEnd.Start();
            new ReminderClock(_ctx.Reminders, nav, timers).Start(); // a minute before a reminder: the card, over whatever is showing
            var saver = new ScreensaverLayer(services.Screensaver);
            _shell.Children.Add(_intro!);       // the start-up animation, over everything until it's done
            _shell.Children.Add(saver.View); // over everything, at native resolution
            ApplyScale(scale);

            // Questions a browser service puts on the video: a prompt ("Skip intro") or a code it sent the viewer.
            router.QuestionChanged += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                promptText.Text = router.PromptText ?? "";
                prompt.Opacity = router.PromptText != null && nav.State.AtVideo ? 1 : 0;
                if (router.CodeRequest != null)
                {
                    ident.Hide();
                    if (nav.State.Overlay?.Name != "code") nav.OpenOverlay(new OverlayRef("code"));
                    else codeEntry?.Refresh();
                }
                else if (nav.State.Overlay?.Name == "code") nav.CloseOverlay();
            });
            router.StatusChanged += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (playback.IsLoading && playback.Current is { Kind: "vod" } cur && router.ActiveBrowser != null && router.StatusText is { Length: > 0 } st)
                    ident.ShowStatus(cur.Title, st);
            });
            _ctx.Starting += card =>
            {
                if (router.BrowserFor(card.StreamUrl) is { } b) ident.ShowStatus(card.Label, router.StatusText is { Length: > 0 } st ? st : $"Starting {b.Name}…");
            };

            _ctx.Tuning += async ch =>
            {
                _ctx.Programme = null;
                ident.Show(ch, null);
                try
                {
                    var prog = await home.CurrentProgrammeAsync(ch.Id);
                    if (playback.Current?.Channel?.Id != ch.Id) return;
                    _ctx.Programme = prog;
                    if (prog != null && ident.Showing) ident.Show(ch, prog);
                }
                catch (Exception ex) { Console.WriteLine($"[NewUi] What's on {ch.Name} failed: {ex.Message}"); }
            };
            playback.Loaded += ident.Loaded;
            nav.Changed += () =>
            {
                if (nav.State.Overlay != null) upNextCard.Opacity = 0;
                if (nav.State.Overlay != null || !nav.State.AtVideo) ident.Hide();
                if (!nav.State.AtVideo) prompt.Opacity = 0;
            };
            // The guide's preview: the one video surface is scaled into the preview's frame (a transform; it is never
            // re-parented, as mpv dies with its GL context) and back to full screen when the guide goes.
            surface.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
            Shell.Motion.Animate(surface, Shell.Motion.Med);
            void FrameVideo()
            {
                var frame = GuideScreen.ShowsPreview(nav.State, playback.Current != null) && !surface.ShowingBrowser;
                var r = GuideHeader.PreviewRect;
                var tl = frame ? _shell.Stage.TranslatePoint(r.TopLeft, surface) : null;
                var br = frame ? _shell.Stage.TranslatePoint(r.BottomRight, surface) : null;
                surface.RenderTransform = tl is { } a && br is { } b && surface.Bounds.Width > 0
                    ? GuideScreen.PreviewTransform(new Rect(a, b), surface.Bounds.Size)
                    : Shell.Motion.None;
            }
            nav.Changed += FrameVideo;
            playback.Changed += FrameVideo;
            playback.Changed += () =>
            {
                surface.HasVideo = playback.Current != null;
                // A browser service plays in the window behind Baird: let it show through once it's playing.
                var browser = router.ActiveBrowser != null && playback.Current != null && !playback.IsLoading && playback.Failure == null;
                surface.ShowingBrowser = browser;
                if (TopLevel.GetTopLevel(this) is Window w) w.Background = browser ? Brushes.Transparent : Brushes.Black;
                surface.RequestNextFrameRendering();
                if (playback.Failure is { } why && playback.Current is { } cur)
                {
                    if (nav.State.AtVideo && router.FailureAction is { } fix) ident.ShowFailure(cur.Title, $"{why}\nPress OK to {fix}.");
                    else if (nav.State.AtVideo && router.ActiveBrowser != null) ident.ShowFailure(cur.Title, why);
                    else { ident.Hide(); _shell.Toast(why); }
                }
            };
            playback.Ended += () =>
            {
                playback.Stop();
                nav.Stop();
                surface.HasVideo = false;
                surface.RequestNextFrameRendering();
            };

            _life = new TvLifecycle(services.Cec, new PlaybackPausable(playback), saver, timers, () => DateTime.UtcNow, a => Dispatcher.UIThread.Post(a),
                inputLock: bool.TryParse(config["BAIRD_ENABLE_INPUT_LOCK"], out var lockOn) && lockOn);
            _ = System.Threading.Tasks.Task.Run(() => services.Screensaver.InitializeAsync());
            _life.Start();

            Content = _shell;
            _holdTick.Tick += (_, _) => { if (_hold.Tick() is { } held) Dispatch(held, false); };
        }

        private void ApplyScale(double scale)
        {
            // Interface size: scales the UI (not the picture) about the centre, for TVs that overscan.
            _shell.Stage.RenderTransformOrigin = RelativePoint.Center;
            _shell.Stage.RenderTransform = new ScaleTransform(scale, scale);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _intro?.Play(() => { _shell.Children.Remove(_intro); _intro = null; });
            if (TopLevel.GetTopLevel(this) is { } top)
            {
                top.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
                top.AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
                // Remote-only: no pointer; the air-mouse remote's buttons are OK, Back and Home.
                top.Cursor = new Cursor(StandardCursorType.None);
                top.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
                top.AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
                top.AddHandler(PointerWheelChangedEvent, (_, e) => e.Handled = true, RoutingStrategies.Tunnel);
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            if (TopLevel.GetTopLevel(this) is { } top)
            {
                top.RemoveHandler(KeyDownEvent, OnKeyDown);
                top.RemoveHandler(KeyUpEvent, OnKeyUp);
                top.RemoveHandler(PointerPressedEvent, OnPointerPressed);
                top.RemoveHandler(PointerReleasedEvent, OnPointerReleased);
            }
            base.OnDetachedFromVisualTree(e);
        }

        private static RemoteInput TvInputOf(KeyEventArgs e) => e.Key switch
        {
            Key.VolumeUp => RemoteInput.VolumeUp,
            Key.VolumeDown => RemoteInput.VolumeDown,
            Key.VolumeMute => RemoteInput.Mute,
            // XF86PowerOff has no Avalonia Key (it arrives as Key.None): match the physical key.
            Key.Sleep => RemoteInput.Power,
            _ when e.PhysicalKey is PhysicalKey.Power or PhysicalKey.Sleep => RemoteInput.Power,
            _ => RemoteInput.Other,
        };

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            // A key during the start-up animation skips it, and does nothing else.
            if (_intro != null && !_intro.IsFinishing) { _intro.Skip(); e.Handled = true; return; }
            // TV first: power/volume go to the TV; a key that wakes the screensaver or the TV does nothing else.
            if (_life.HandleInput(TvInputOf(e))) { e.Handled = true; return; }
            if (_input.Map(e.Key) is not { } action) return;
            e.Handled = true;
            var repeat = _lastKey == e.Key;
            _lastKey = e.Key;
            // OK is decided on release: a tap is OK, a hold (500 ms) is "more options".
            if (action == UiAction.Ok) { _hold.Down(); _holdTick.Start(); return; }
            Dispatch(action, repeat);
        }

        private void OnKeyUp(object? sender, KeyEventArgs e)
        {
            if (_lastKey == e.Key) _lastKey = null;
            if (_input.Map(e.Key) is not { } action) return;
            e.Handled = true;
            if (action == UiAction.Ok)
            {
                _holdTick.Stop();
                if (_hold.Up() is { } ok) Dispatch(ok, false);
            }
        }

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            var kind = e.GetCurrentPoint(null).Properties.PointerUpdateKind;
            Console.WriteLine($"[NewUi] Remote button: {kind}");
            if (_life.HandleInput(RemoteInput.Other)) return;
            switch (InputMap.MapButton(kind))
            {
                case UiAction.Ok: _hold.Down(); _holdTick.Start(); break; // decided on release, like the OK key
                case { } action: Dispatch(action, false); break;
            }
        }

        private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            e.Handled = true;
            if (InputMap.MapButton(e.GetCurrentPoint(null).Properties.PointerUpdateKind) != UiAction.Ok) return;
            _holdTick.Stop();
            if (_hold.Up() is { } ok) Dispatch(ok, false);
        }

        private void Dispatch(UiAction action, bool repeat)
        {
            try { _shell.OnAction(action, repeat); }
            catch (Exception ex) { Console.WriteLine($"[NewUi] {action} failed: {ex}"); }
        }
    }

    /// <summary>The playback controller as the TV lifecycle sees it.</summary>
    internal sealed class PlaybackPausable : IPausable
    {
        private readonly PlaybackController _p;
        public PlaybackPausable(PlaybackController p) => _p = p;
        public bool IsPlaying => _p.Current != null && !_p.IsPaused && !_p.IsLoading;
        public void Pause() { if (IsPlaying) _p.TogglePause(); }
        public void Resume() { if (_p.Current != null && _p.IsPaused) _p.TogglePause(); }
    }
}
