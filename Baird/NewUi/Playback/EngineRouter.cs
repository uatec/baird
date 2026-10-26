using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baird.Mpv;
using Baird.Playback;

namespace Baird.NewUi.Playback
{
    /// <summary>
    /// One engine for the playback controller: plays each URL in mpv, or in the browser engine (Netflix, Disney+, ITVX,
    /// Channel 4) whose CanPlay accepts it. Only the active engine's events come through; switching stops the previous one.
    /// </summary>
    public sealed class EngineRouter : IPlaybackEngine
    {
        private readonly IPlaybackEngine _mpv;
        private readonly IReadOnlyList<IBrowserPlaybackEngine> _browsers;

        public IPlaybackEngine Active { get; private set; }
        public IBrowserPlaybackEngine? ActiveBrowser => Active as IBrowserPlaybackEngine;

        public event EventHandler? FileLoaded;
        public event EventHandler? StreamEnded;
        public event EventHandler<bool>? PauseStateChanged;
        public event EventHandler<double>? DurationChanged;
        /// <summary>The browser engine's loading step changed (StatusText).</summary>
        public event EventHandler? StatusChanged;
        /// <summary>The browser engine couldn't play: the reason, for the viewer.</summary>
        public event EventHandler<string>? Failed;
        /// <summary>A prompt ("Skip intro") or a code request appeared, changed or went away.</summary>
        public event EventHandler? QuestionChanged;

        public EngineRouter(IPlaybackEngine mpv, IEnumerable<IBrowserPlaybackEngine> browsers)
        {
            _mpv = mpv; _browsers = browsers.ToList(); Active = mpv;
            foreach (var e in _browsers.Cast<IPlaybackEngine>().Prepend(mpv)) Subscribe(e);
        }

        private void Subscribe(IPlaybackEngine e)
        {
            e.FileLoaded += (_, a) => { if (Active == e) FileLoaded?.Invoke(this, a); };
            e.StreamEnded += (_, a) => { if (Active == e) StreamEnded?.Invoke(this, a); };
            e.PauseStateChanged += (_, a) => { if (Active == e) PauseStateChanged?.Invoke(this, a); };
            e.DurationChanged += (_, a) => { if (Active == e) DurationChanged?.Invoke(this, a); };
            if (e is IBrowserPlaybackEngine b)
            {
                b.StatusChanged += (_, a) => { if (Active == e) StatusChanged?.Invoke(this, a); };
                b.Failed += (_, reason) => { if (Active == e) Failed?.Invoke(this, reason); };
            }
            if (e is IPromptingEngine p) p.PromptChanged += (_, a) => { if (Active == e) QuestionChanged?.Invoke(this, a); };
            if (e is ICodeEntryEngine c) c.CodeRequestChanged += (_, a) => { if (Active == e) QuestionChanged?.Invoke(this, a); };
        }

        /// <summary>The browser engine that would play this URL, if any.</summary>
        public IBrowserPlaybackEngine? BrowserFor(string? url) => _browsers.FirstOrDefault(b => b.CanPlay(url));

        public PlaybackState State => Active.State;
        public double TimePositionSeconds => Active.TimePositionSeconds;
        public double DurationSeconds => Active.DurationSeconds;

        private static IDisposable Doing(string what) => Diagnostics.Breadcrumbs.Shared.Doing(what);

        public void Play(string url, double? startSeconds = null)
        {
            using var _ = Doing($"engine Play ({(BrowserFor(url)?.Name ?? "mpv")})");
            var next = (IPlaybackEngine?)BrowserFor(url) ?? _mpv;
            if (next != Active)
            {
                Console.WriteLine($"[EngineRouter] Switching to {(next is IBrowserPlaybackEngine b ? b.Name : "mpv")}.");
                Active.Stop();
                Active = next;
            }
            Active.Play(url, startSeconds);
        }

        public void Pause() { using var _ = Doing("engine Pause"); Active.Pause(); }
        public void Resume() { using var _ = Doing("engine Resume"); Active.Resume(); }
        public void Seek(double seconds) { using var _ = Doing("engine Seek"); Active.Seek(seconds); }
        public void SeekRelative(double seconds) { using var _ = Doing("engine SeekRelative"); Active.SeekRelative(seconds); }
        public void SetSubtitle(bool enabled) { using var _ = Doing("engine SetSubtitle"); Active.SetSubtitle(enabled); }
        public void Stop() { using var _ = Doing("engine Stop"); Active.Stop(); }

        // ---- the questions a browser engine can put on the video
        public string? StatusText => ActiveBrowser?.StatusText;
        public string? PromptText => (Active as IPromptingEngine)?.PromptText;
        public bool ActivatePrompt() => Active is IPromptingEngine p && p.ActivatePrompt();
        public string? CodeRequest => (Active as ICodeEntryEngine)?.CodeRequest;
        public Task SubmitCodeAsync(string code) => Active is ICodeEntryEngine c ? c.SubmitCodeAsync(code) : Task.CompletedTask;
        public string? FailureAction => (Active as IFailureActionEngine)?.FailureAction;
        public Task<string?> RunFailureActionAsync(CancellationToken ct = default) =>
            Active is IFailureActionEngine f ? f.RunFailureActionAsync(ct) : Task.FromResult<string?>(null);
    }
}
