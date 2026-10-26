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
            var playback = new PlaybackController(mpv, a => Dispatcher.UIThread.Post(a), timers, () => DateTimeOffset.Now,
                (id, pos, dur) => _ = history.SaveAsync(id, pos, dur));
            var home = new HomeData(services.Data, services.Epg, services.Seerr);
            _ctx = new NewUiContext(nav, playback, home, () => DateTimeOffset.Now);
            var videoActions = new VideoActions(_ctx, timers);
            var ident = new TuneIdent();

            var scale = double.TryParse(config["Baird:UiScale"], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 1.0;
            _shell = new Shell.Shell(new Shell.ShellDeps(
                surface, nav,
                CreatePlace: p => p switch
                {
                    Place.Home => new HomeScreen(_ctx),
                    Place.LiveTv => new LiveTvPlaceholder(_ctx),
                    Place.Search => new SearchPlaceholder(),
                    _ => new SettingsScreen(() => UiMode.New, mode => { UiModes.Save(mode); AppRestarter.ForThisProcess().Restart(); }, scale, v =>
                    {
                        ApplyScale(v);
                        IniSettingWriter.Save(IniSettingWriter.DefaultPath, "Baird", "UiScale", v.ToString("0.0", CultureInfo.InvariantCulture));
                    }),
                },
                CreatePage: _ => new EmptyScreen(),
                CreateOverlay: o => o.Name == "info" ? new InfoBar(_ctx) : new EmptyScreen(),
                VideoAction: videoActions.OnAction,
                BackToVideoLabel: () => playback.Current?.Title ?? "what's playing"));
            _ctx.Toast = _shell.Toast;
            _ctx.FlashBackChip = _shell.FlashBackChip;
            _shell.Stage.Children.Insert(0, ident); // over the picture, under everything else
            ApplyScale(scale);

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
            nav.Changed += () => { if (nav.State.Overlay != null || !nav.State.AtVideo) ident.Hide(); };
            playback.Changed += () =>
            {
                surface.HasVideo = playback.Current != null;
                surface.RequestNextFrameRendering();
                if (playback.Failure != null) { ident.Hide(); _shell.Toast(playback.Failure); }
            };
            playback.Ended += () =>
            {
                playback.Stop();
                nav.Stop();
                surface.HasVideo = false;
                surface.RequestNextFrameRendering();
            };

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
            if (TopLevel.GetTopLevel(this) is { } top)
            {
                top.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
                top.AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            if (TopLevel.GetTopLevel(this) is { } top)
            {
                top.RemoveHandler(KeyDownEvent, OnKeyDown);
                top.RemoveHandler(KeyUpEvent, OnKeyUp);
            }
            base.OnDetachedFromVisualTree(e);
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
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

        private void Dispatch(UiAction action, bool repeat)
        {
            try { _shell.OnAction(action, repeat); }
            catch (Exception ex) { Console.WriteLine($"[NewUi] {action} failed: {ex}"); }
        }
    }
}
