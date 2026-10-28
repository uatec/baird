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
        private readonly ICecService _cec;
        private CecRemoteControl? _tvRemote;
        private readonly Key _voiceKey;
        private readonly VoiceSearch? _voice;
        private Intro? _intro = new();

        public NewShellView(IConfiguration config)
        {
            Background = Brushes.Transparent;
            var voiceKey = int.TryParse(config["VOICE_COMMAND_KEY"], out var vk) && vk != 0 ? (Key)vk : Key.None;
            _input = new InputMap(voiceKey);
            _voiceKey = voiceKey;

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
            var home = new HomeData(services.Data, services.Epg, services.Seerr, new ProgrammeArt(services.Seerr, ProgrammeArt.DefaultPath)) { HiddenPath = HomeData.DefaultHiddenPath };
            _ctx = new NewUiContext(nav, playback, home, () => DateTimeOffset.Now) { Timers = timers };
            _ctx.Search = new CatalogueSearch(services.Data, services.Epg, services.Seerr, new SearchHistoryService());
            _ctx.Shows = new ShowData(services.Data, services.Seerr, _ctx.Search);
            // Netflix searches in the one shared browser: while another service plays there, say so instead of silently missing it.
            _ctx.SearchNote = () => services.Netflix is { } nf && nf.Hub.ActivePlayback is { } busy && nf.DirectSearchWorks != true
                ? $"Netflix search is paused while {busy} plays — stop it to search Netflix." : null;
            // An episode started from Home (not its show page): find its show, so the end card can offer the next one.
            _ctx.FindFollowing = async card =>
            {
                if (!card.Source.StartsWith("Jellyfin", StringComparison.OrdinalIgnoreCase)) return null;
                if (await services.Jellyfin.GetSeriesOfAsync(card.Id) is not { } series) return null;
                var title = new Model.CatalogueTitle($"Series:{series.Id}", Model.TitleKind.Series, series.Name, null, null, null,
                    new[] { new Model.TitleSource("Jellyfin", series.Id, Model.PlayVia.Mpv) });
                var details = await _ctx.Shows!.DetailsAsync(title);
                var eps = details.Seasons.SelectMany(s => s.Episodes)
                    .Where(e => e.State == Model.EpisodeState.Available && e.Source is { StreamUrl: not null })
                    .Select(e => new Model.HomeCard(Model.CardKind.Episode, e.Source!.ItemId, e.Label, e.Code, e.ImageUrl, null, null, e.Source.Service, e.Source.Via, e.Source.StreamUrl, e.Source.Resume, Synopsis: e.Synopsis))
                    .ToList();
                return (eps, series.Name);
            };
            _ctx.OpenGuide = (id, at) => GuideScreen.OpenSchedule(_shell!, nav, id, at);
            _ctx.Prefetch = new Prefetcher(_ctx.Shows, timers);
            var guideData = new GuideData(home, services.Epg);
            _ctx.Reminders = new Reminders(Reminders.DefaultPath, () => DateTimeOffset.Now);
            _ctx.SetGuideLayout(string.Equals(config["Baird:GuideLayout"], "columns", StringComparison.OrdinalIgnoreCase) ? Model.GuideLayout.Columns : Model.GuideLayout.Rows, save: false);
            _ctx.SaveGuideLayout = l => IniSettingWriter.Save(IniSettingWriter.DefaultPath, "Baird", "GuideLayout", l == Model.GuideLayout.Columns ? "columns" : "rows");
            // Settings: subtitles and autoplay (Baird:Subtitles / Baird:Autoplay in config.ini, on unless set to false).
            static bool Flag(string? v) => v == null || !(v.Equals("false", StringComparison.OrdinalIgnoreCase) || v.Equals("off", StringComparison.OrdinalIgnoreCase) || v == "0");
            _ctx.Autoplay = Flag(config["Baird:Autoplay"]);
            playback.SubtitlesDefault = Flag(config["Baird:Subtitles"]);
            var updates = UpdateService.ForThisMachine(config);
            var sourceStatus = SourceStatusReader.For(services, () => _ctx.Channels.Count, !string.IsNullOrEmpty(config["JELLYSEERR_API_KEY"]), () => DateTimeOffset.Now);
            var settingsOptions = new SettingsOptions(
                () => playback.SubtitlesDefault,
                v => { playback.SubtitlesDefault = v; IniSettingWriter.Save(IniSettingWriter.DefaultPath, "Baird", "Subtitles", v ? "true" : "false"); },
                () => _ctx.Autoplay,
                v => { _ctx.Autoplay = v; IniSettingWriter.Save(IniSettingWriter.DefaultPath, "Baird", "Autoplay", v ? "true" : "false"); },
                () => sourceStatus.ReadAsync(),
                About: $"{updates.Current} · new interface");
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
                    }, () => _ctx.GuideLayout, l => _ctx.SetGuideLayout(l), updates, settingsOptions),
                },
                CreatePage: p => p.Name == "show" && p.Params is Model.CatalogueTitle t ? new ShowPage(_ctx, t)
                    : p.Name == Shell.Shell.GuidePage ? GuideScreen.ForPage(_ctx, guideData.LoadAsync, timers, p) : new EmptyScreen(),
                CreateOverlay: o => o.Name switch
                {
                    "info" => new InfoBar(_ctx),
                    "number" when o.Params is int first => new NumberPanel(_ctx, timers, first),
                    "channels" => new ChannelList(_ctx, timers, o.Params as UiAction?, guideData.LoadAsync),
                    "actions" => new ActionsPanel(_ctx, () => GuideScreen.OpenSchedule(_shell!, nav, playback.Current?.Channel?.Id, DateTimeOffset.Now)),
                    "reminder" when o.Params is Model.Reminder r => new ReminderCard(_ctx, r),
                    "code" => codeEntry = new CodeEntry(() => router.CodeRequest, router.SubmitCodeAsync, () => { playback.Stop(); nav.Stop(); }),
                    "itemactions-upnext" when o.Params is Model.HomeCard upNext => new ItemActions(_ctx, upNext, id => GuideScreen.OpenSchedule(_shell!, nav, id, DateTimeOffset.Now), fromUpNext: true),
                    "itemactions" when o.Params is Model.HomeCard card => new ItemActions(_ctx, card, id => GuideScreen.OpenSchedule(_shell!, nav, id, DateTimeOffset.Now)),
                    "upnext" when o.Params is Model.HomeCard next => new UpNextCard(_ctx, timers, next),
                    _ when o.Params is ChoiceList list => new ChoiceSheet(_ctx, list),
                    _ => new EmptyScreen(),
                },
                VideoAction: videoActions.OnAction,
                BackToVideoLabel: () => playback.Current?.Title ?? "what's playing"));
            _ctx.Toast = _shell.Toast;
            _ctx.FlashBackChip = _shell.FlashBackChip;
            _shell.Stage.Children.Insert(0, ident); // over the picture, under everything else
            // Radio has no picture: the channel's logo and what's on fill the screen instead (under the ident and every screen).
            var radio = new RadioCard();
            radio.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
            Shell.Motion.Animate(radio, Shell.Motion.Med);
            _shell.Stage.Children.Insert(0, radio);
            void RefreshRadio() => radio.Update(
                playback.Current is { Kind: "live", Channel: { } rc } && playback.IsAudioOnly && !playback.IsLoading && playback.Failure == null ? rc : null,
                _ctx.Programme, DateTimeOffset.Now);
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
                RefreshRadio();
                var prog = _ctx.Programme;
                if (playback.Current is not { Kind: "live", Channel: { } ch } || !nav.State.AtVideo || nav.State.Overlay != null || !liveUpNext.ShouldShow(prog, DateTimeOffset.Now))
                {
                    if (upNextCard.Opacity > 0 && (prog == null || DateTimeOffset.Now >= prog.End || !nav.State.AtVideo)) upNextCard.Opacity = 0;
                    return;
                }
                if (upNextCard.Opacity > 0) return;
                var next = await home.NextProgrammeAsync(ch.Id, DateTimeOffset.Now);
                if (next == null || prog == null) return;
                var after = await home.NextProgrammeAsync(ch.Id, next.Start);
                upNextText.Inlines = LiveUpNext.Inlines(ch, prog, next, after, DateTimeOffset.Now);
                upNextCard.Opacity = 1;
                liveUpNext.Dismiss(prog);
                DispatcherTimer.RunOnce(() => upNextCard.Opacity = 0, TimeSpan.FromSeconds(20));
            };
            nearEnd.Start();
            new ReminderClock(_ctx.Reminders, nav, timers).Start(); // a minute before a reminder: the card, over whatever is showing
            var saver = new ScreensaverLayer(services.Screensaver);
            _shell.Children.Add(_intro!);       // the start-up animation, over everything until it's done
            var stillWatching = new StillWatchingPrompt();
            _shell.Children.Add(stillWatching.View);   // "Still watching?", under only the screensaver
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
                    RefreshRadio();
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
                // The radio card fills the Stage, so the preview frame (in Stage coordinates) is its target as it stands.
                radio.SetCompact(frame);
                radio.RenderTransform = frame && radio.Bounds.Width > 0
                    ? GuideScreen.PreviewTransform(r, radio.Bounds.Size)
                    : Shell.Motion.None;
            }
            nav.Changed += FrameVideo;
            playback.Changed += FrameVideo;
            playback.Changed += () =>
            {
                RefreshRadio();
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
                // The end of an episode: offer the next one (the card plays it by itself after a countdown).
                if (playback.Current is { Kind: "vod" } && _ctx.NextEpisode() is { } next)
                {
                    nav.OpenOverlay(new OverlayRef("upnext", next));
                    return;
                }
                playback.Stop();
                nav.Stop();
                surface.HasVideo = false;
                surface.RequestNextFrameRendering();
            };

            if (voiceKey != Key.None)
                _voice = new VoiceSearch(new WhisperSpeechToTextService(config), text =>
                {
                    _shell.ShowPlace(Place.Search); // already on Search: stay put (no re-entry, focus kept)
                    if (_shell.PlaceScreen(Place.Search) is SearchScreen search) search.SetQuery(text);
                }, _shell.Toast);
            // Motion budget measurement: BAIRD_AV_LOG=true logs dropped frames every 5 s with what's on screen.
            if (bool.TryParse(config["BAIRD_AV_LOG"], out var avLog) && avLog)
            {
                long? lastVo = null, lastDec = null;
                var av = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                av.Tick += async (_, _) =>
                {
                    if (playback.Current == null || router.ActiveBrowser != null) { lastVo = lastDec = null; return; }
                    var st = await mpv.SampleAvStatsAsync();
                    if (st == null) return;
                    var where = nav.State.Overlay?.Name ?? (nav.State.Pages.Count > 0 ? nav.State.Pages[^1].Name : nav.State.AtVideo ? "video" : nav.State.Place.ToString());
                    Console.WriteLine($"[AvLog] screen={where} vo_drops={st.VoDrops - (lastVo ?? st.VoDrops)} dec_drops={st.DecoderDrops - (lastDec ?? st.DecoderDrops)} total_vo={st.VoDrops} fps={st.EstimatedVideoFps:0.0}");
                    lastVo = st.VoDrops; lastDec = st.DecoderDrops;
                };
                av.Start();
            }
            // Freeze diagnostics: a watchdog notices the UI thread not answering and writes ~/.baird/freezes/freeze-*.txt.
            var crumbs = Diagnostics.Breadcrumbs.Shared;
            nav.Changed += () => crumbs.Add($"screen {DescribeScreen()}{(nav.State.Video != null ? " (video behind)" : "")}");
            playback.Loaded += () => crumbs.Add($"playing {playback.Current?.Title}");
            playback.Ended += () => crumbs.Add($"ended {playback.Current?.Title}");
            playback.Changed += () => { if (playback.Failure is { } f) crumbs.Add($"playback failed: {f}"); };
            new Diagnostics.FreezeWatchdog(a => Dispatcher.UIThread.Post(a, DispatcherPriority.Send), () => DateTime.Now, Diagnostics.FreezeWatchdog.DefaultDir, crumbs,
                () => $"screen {DescribeScreen()}; playing {(playback.Current is { } c ? $"{c.Kind} '{c.Title}'{(playback.IsLoading ? " (loading)" : "")}{(playback.IsPaused ? " (paused)" : "")} via {(router.ActiveBrowser?.Name ?? "mpv")}" : "nothing")}; " +
                      $"intro {(_intro != null ? "showing" : "done")}; screensaver {(saver.IsActive ? "on" : "off")}").Start();
            Diagnostics.ResourceRecorder.ForThisProcess().Start();   // ~/.baird/resources.log, a line a minute
            _cec = services.Cec;
            _life = new TvLifecycle(services.Cec, new PlaybackPausable(playback), saver, timers, () => DateTime.UtcNow, a => Dispatcher.UIThread.Post(a),
                inputLock: bool.TryParse(config["BAIRD_ENABLE_INPUT_LOCK"], out var lockOn) && lockOn, stillWatching, new FileIdleState());
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
            _intro?.Play(() => { _shell.Children.Remove(_intro); _intro = null; }, (_shell.PlaceScreen(Place.Home) as HomeScreen)?.Ready);
            if (TopLevel.GetTopLevel(this) is { } top)
            {
                top.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
                top.AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
                // Remote-only: no pointer; the air-mouse remote's buttons are OK, Back and Home.
                top.Cursor = new Cursor(StandardCursorType.None);
                top.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
                top.AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
                top.AddHandler(PointerWheelChangedEvent, (_, e) => e.Handled = true, RoutingStrategies.Tunnel);
                // The TV's own remote: keys it passes on over CEC arrive as the same key presses as the USB remote's.
                _tvRemote = new CecRemoteControl(_cec, (key, down) => Dispatcher.UIThread.Post(() => global::Baird.Controls.SyntheticKeyInput.Raise(top, key, down)));
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
            _tvRemote?.Dispose();
            _tvRemote = null;
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
            // The voice key: hold to talk, let go to search.
            if (_voice != null && e.Key == _voiceKey) { e.Handled = true; _ = _voice.PressAsync(); return; }
            // TV first: power/volume go to the TV; a key that wakes the screensaver or the TV does nothing else.
            if (_life.HandleInput(TvInputOf(e))) { e.Handled = true; return; }
            // A real keyboard on a text field types (H, G, Space… would otherwise be Home, Guide, pause).
            if (_shell.AcceptsText && InputMap.TryText(e.Key, e.KeyModifiers, e.KeySymbol, out var typed))
            {
                e.Handled = true;
                _shell.TypeText(typed);
                return;
            }
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
            if (_voice != null && e.Key == _voiceKey) { e.Handled = true; _ = _voice.ReleaseAsync(); return; }
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

        private readonly ToggleGate _toggles = new();

        private string DescribeScreen()
        {
            var st = _ctx.Nav.State;
            return st.Overlay?.Name ?? (st.Pages.Count > 0 ? st.Pages[^1].Name : st.AtVideo ? "video" : st.Place.ToString());
        }

        private void Dispatch(UiAction action, bool repeat)
        {
            if (!_toggles.Pass(action, repeat, DateTime.UtcNow)) return;
            var crumbs = Diagnostics.Breadcrumbs.Shared;
            if (!repeat) crumbs.Add($"key {action}");
            using var _ = crumbs.Doing($"{action}{(repeat ? " (held)" : "")} on {DescribeScreen()}");
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
        public bool HasStream => _p.Current != null;
        public void Pause() { if (IsPlaying) _p.TogglePause(); }
        public void Resume() { if (_p.Current != null && _p.IsPaused) _p.TogglePause(); }
    }
}
