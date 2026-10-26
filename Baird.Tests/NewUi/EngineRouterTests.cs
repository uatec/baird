using System;
using System.Collections.Generic;
using System.Linq;
using Baird.Mpv;
using Baird.NewUi.Playback;
using Baird.Playback;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class EngineRouterTests
    {
        internal sealed class FakeBrowser : IBrowserPlaybackEngine, IPromptingEngine
        {
            private readonly string _prefix;
            public FakeBrowser(string name, string prefix) { Name = name; _prefix = prefix; }
            public string Name { get; }
            public readonly List<string> Calls = new();
            public bool CanPlay(string? url) => url?.StartsWith(_prefix) == true;
            public string StatusText { get; set; } = "Starting";
            public string? PromptText { get; set; }
            public PlaybackState State { get; set; } = PlaybackState.Idle;
            public double TimePositionSeconds => 0;
            public double DurationSeconds => 0;
            public event EventHandler? StatusChanged;
            public event EventHandler<string>? Failed;
            public event EventHandler? PromptChanged;
            public event EventHandler? FileLoaded;
            public event EventHandler? StreamEnded;
            public event EventHandler<bool>? PauseStateChanged;
            public event EventHandler<double>? DurationChanged;
            public bool ActivatePrompt() { Calls.Add("prompt"); return PromptText != null; }
            public void Play(string url, double? s = null) { Calls.Add("play " + url); State = PlaybackState.Loading; }
            public void Pause() => Calls.Add("pause");
            public void Resume() => Calls.Add("resume");
            public void Seek(double s) { }
            public void SeekRelative(double s) => Calls.Add($"rel {s}");
            public void SetSubtitle(bool e) { }
            public void Stop() { Calls.Add("stop"); State = PlaybackState.Idle; }
            public void Load() => FileLoaded?.Invoke(this, EventArgs.Empty);
            public void Fail(string why) => Failed?.Invoke(this, why);
            public void Status(string s) { StatusText = s; StatusChanged?.Invoke(this, EventArgs.Empty); }
            public void Prompt(string? p) { PromptText = p; PromptChanged?.Invoke(this, EventArgs.Empty); }
            void Unused() { StreamEnded?.Invoke(this, EventArgs.Empty); PauseStateChanged?.Invoke(this, true); DurationChanged?.Invoke(this, 0); }
        }

        private static (EngineRouter R, PlaybackControllerTests.FakeEngine Mpv, FakeBrowser Netflix) Build()
        {
            var mpv = new PlaybackControllerTests.FakeEngine();
            var nf = new FakeBrowser("Netflix", "https://www.netflix.com/");
            return (new EngineRouter(mpv, new[] { nf }), mpv, nf);
        }

        [Fact]
        public void Urls_go_to_the_engine_that_plays_them_and_the_previous_one_stops()
        {
            var (r, mpv, nf) = Build();
            r.Play("http://tvh/101");
            Assert.Equal("http://tvh/101", mpv.Plays.Single().Url);
            r.Play("https://www.netflix.com/watch/1");
            Assert.Contains("stop", mpv.Calls);
            Assert.Equal(new[] { "play https://www.netflix.com/watch/1" }, nf.Calls);
            Assert.Same(nf, r.ActiveBrowser);
            r.Play("http://tvh/102");
            Assert.Equal(new[] { "play https://www.netflix.com/watch/1", "stop" }, nf.Calls);
            Assert.Null(r.ActiveBrowser);
        }

        [Fact]
        public void Only_the_active_engines_events_come_through()
        {
            var (r, mpv, nf) = Build();
            var loaded = 0; var failed = new List<string>();
            r.FileLoaded += (_, _) => loaded++; r.Failed += (_, w) => failed.Add(w);
            r.Play("http://tvh/101");
            nf.Load(); nf.Fail("old");
            Assert.Equal(0, loaded); Assert.Empty(failed);
            mpv.Loaded();
            Assert.Equal(1, loaded);
            r.Play("https://www.netflix.com/watch/1");
            nf.Fail("Netflix isn't signed in");
            Assert.Equal(new[] { "Netflix isn't signed in" }, failed);
        }

        [Fact]
        public void Controls_and_questions_go_to_the_active_engine()
        {
            var (r, _, nf) = Build();
            var asked = 0; r.QuestionChanged += (_, _) => asked++;
            r.Play("https://www.netflix.com/watch/1");
            nf.Prompt("Skip intro");
            Assert.Equal(1, asked);
            Assert.Equal("Skip intro", r.PromptText);
            Assert.True(r.ActivatePrompt());
            r.SeekRelative(30);
            Assert.Contains("rel 30", nf.Calls);
            nf.Status("Signing in");
            Assert.Equal("Signing in", r.StatusText);
        }
    }
}
