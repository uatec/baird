using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Baird.NewUi.Lifecycle;
using Baird.Services;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class TvLifecycleTests
    {
        private sealed class Cec : ICecService
        {
            public readonly List<string> Calls = new();
            public bool PowerOnResult = true;
            public event EventHandler<CecCommandLoggedEventArgs>? CommandLogged;
            public event EventHandler? TvStandby;
            public event EventHandler? TvPowerOn;
            public event EventHandler? InputLost;
            public event EventHandler? InputRegained;
            public event EventHandler<CecUserControlEventArgs>? UserControlReceived { add { } remove { } }
            public bool IsAvailable { get; set; } = true;
            public void Standby() => TvStandby?.Invoke(this, EventArgs.Empty);
            public void PoweredOn() => TvPowerOn?.Invoke(this, EventArgs.Empty);
            public void Lost() => InputLost?.Invoke(this, EventArgs.Empty);
            public void Regained() => InputRegained?.Invoke(this, EventArgs.Empty);
            public Task StartAsync() { Calls.Add("start"); return Task.CompletedTask; }
            public Task<bool> TogglePowerAsync() { Calls.Add("toggle"); return Task.FromResult(PowerOnResult); }
            public Task PowerOnAsync() { Calls.Add("on"); return Task.CompletedTask; }
            public Task PowerOffAsync() { Calls.Add("off"); return Task.CompletedTask; }
            public Task VolumeUpAsync() { Calls.Add("vol+"); return Task.CompletedTask; }
            public Task VolumeDownAsync() { Calls.Add("vol-"); return Task.CompletedTask; }
            public Task MuteAsync() { Calls.Add("mute"); return Task.CompletedTask; }
            public Task ChangeInputToThisDeviceAsync() { Calls.Add("input"); return Task.CompletedTask; }
            public Task CycleInputsAsync() => Task.CompletedTask;
            public Task<TvPowerStatus> GetPowerStatusAsync() => Task.FromResult(TvPowerStatus.Unknown);
            void Unused() => CommandLogged?.Invoke(this, new CecCommandLoggedEventArgs());
        }

        private sealed class Player : IPausable
        {
            public bool IsPlaying { get; set; }
            public readonly List<string> Calls = new();
            public void Pause() { Calls.Add("pause"); IsPlaying = false; }
            public void Resume() { Calls.Add("resume"); IsPlaying = true; }
        }

        private sealed class Saver : IScreensaver
        {
            public bool IsActive { get; private set; }
            public void Activate() => IsActive = true;
            public void Deactivate() => IsActive = false;
        }

        private sealed record Rig(TvLifecycle Life, Cec Cec, Player Player, Saver Saver, PlaybackControllerTests.FakeTimers Timers);

        private static Rig Build(bool playing = false, bool inputLock = true)
        {
            var cec = new Cec(); var player = new Player { IsPlaying = playing }; var saver = new Saver(); var t = new PlaybackControllerTests.FakeTimers();
            var life = new TvLifecycle(cec, player, saver, t, () => t.Now.UtcDateTime, a => a(), inputLock);
            life.Start();
            Assert.Equal(new[] { "start", "input" }, cec.Calls); // claims the TV at start-up…
            cec.Regained();                                       // …and the TV confirms
            cec.Calls.Clear();
            return new Rig(life, cec, player, saver, t);
        }

        [Fact]
        public void The_power_key_toggles_the_tv_once_per_press()
        {
            var r = Build();
            Assert.True(r.Life.HandleInput(RemoteInput.Power));
            r.Timers.Advance(TimeSpan.FromMilliseconds(300));
            Assert.True(r.Life.HandleInput(RemoteInput.Power));
            Assert.Equal(1, r.Cec.Calls.Count(c => c == "toggle"));
            r.Timers.Advance(TvLifecycle.PowerCooldown);
            r.Life.HandleInput(RemoteInput.Power);
            Assert.Equal(2, r.Cec.Calls.Count(c => c == "toggle"));
        }

        [Fact]
        public void Turning_the_tv_off_pauses_what_is_playing_and_turning_it_on_resumes_it()
        {
            var r = Build(playing: true);
            r.Cec.PowerOnResult = false; // the toggle turned it off
            r.Life.HandleInput(RemoteInput.Power);
            Assert.Equal(new[] { "pause" }, r.Player.Calls);
            Assert.True(r.Life.InputsBlocked);
            r.Cec.Regained();
            Assert.Equal(new[] { "pause", "resume" }, r.Player.Calls);
            Assert.False(r.Life.InputsBlocked);
        }

        [Fact]
        public void Volume_keys_go_to_the_tv_and_a_held_key_is_throttled()
        {
            var r = Build();
            Assert.True(r.Life.HandleInput(RemoteInput.VolumeUp));
            r.Timers.Advance(TimeSpan.FromMilliseconds(40));
            r.Life.HandleInput(RemoteInput.VolumeUp);
            r.Life.HandleInput(RemoteInput.Mute);
            Assert.Equal(new[] { "vol+", "mute" }, r.Cec.Calls);
        }

        [Fact]
        public void Tv_standby_pauses_and_getting_the_input_back_resumes()
        {
            var r = Build(playing: true);
            r.Cec.Standby();
            Assert.Equal(new[] { "pause" }, r.Player.Calls);
            r.Cec.Regained();
            Assert.Equal(new[] { "pause", "resume" }, r.Player.Calls);
        }

        [Fact]
        public void Standby_while_paused_does_not_resume_later()
        {
            var r = Build(playing: false);
            r.Cec.Standby();
            r.Cec.Regained();
            Assert.Empty(r.Player.Calls);
        }

        [Fact]
        public void After_two_idle_hours_the_screensaver_starts_and_the_next_key_only_wakes_it()
        {
            var r = Build(playing: true);
            r.Timers.Advance(TvLifecycle.IdleTimeout);
            Assert.True(r.Saver.IsActive);
            Assert.Equal(new[] { "pause" }, r.Player.Calls);
            Assert.True(r.Life.HandleInput(RemoteInput.Other));
            Assert.False(r.Saver.IsActive);
            Assert.Equal(new[] { "pause", "resume" }, r.Player.Calls);
            Assert.False(r.Life.HandleInput(RemoteInput.Other));
        }

        [Fact]
        public void Activity_keeps_the_screensaver_away()
        {
            var r = Build();
            r.Timers.Advance(TvLifecycle.IdleTimeout - TimeSpan.FromMinutes(1));
            r.Life.HandleInput(RemoteInput.Other);
            r.Timers.Advance(TimeSpan.FromMinutes(5));
            Assert.False(r.Saver.IsActive);
        }

        [Fact]
        public void Activity_wakes_the_tv_and_claims_the_input_at_most_every_30_seconds()
        {
            var r = Build();
            r.Life.HandleInput(RemoteInput.Other);
            r.Timers.Advance(TimeSpan.FromSeconds(5));
            r.Life.HandleInput(RemoteInput.Other);
            Assert.Equal(new[] { "on", "input" }, r.Cec.Calls);
            r.Timers.Advance(TvLifecycle.AssertCooldown);
            r.Life.HandleInput(RemoteInput.Other);
            Assert.Equal(4, r.Cec.Calls.Count);
        }

        [Fact]
        public void While_another_device_has_the_tv_keys_ask_for_it_back_and_do_nothing_else()
        {
            var r = Build();
            r.Cec.Lost();
            Assert.True(r.Life.HandleInput(RemoteInput.Other));
            Assert.Contains("input", r.Cec.Calls);
            r.Timers.Advance(TvLifecycle.UnblockFallback); // the TV switched without saying so
            Assert.False(r.Life.InputsBlocked);
            Assert.False(r.Life.HandleInput(RemoteInput.Other));
        }

        [Fact]
        public void The_tv_waking_for_another_device_does_not_steal_its_input()
        {
            var r = Build();
            r.Cec.Lost();
            r.Cec.Calls.Clear();
            r.Cec.PoweredOn();
            Assert.DoesNotContain("input", r.Cec.Calls);
        }

        [Fact]
        public void At_start_up_keys_wait_until_the_tv_confirms_the_input()
        {
            var cec = new Cec(); var t = new PlaybackControllerTests.FakeTimers();
            var life = new TvLifecycle(cec, new Player(), new Saver(), t, () => t.Now.UtcDateTime, a => a(), inputLock: true);
            life.Start();
            Assert.True(life.InputsBlocked);
            Assert.True(life.HandleInput(RemoteInput.Other));
            cec.Regained();
            Assert.False(life.HandleInput(RemoteInput.Other));
        }

        [Fact]
        public void Without_the_input_lock_keys_still_work_after_the_input_is_lost()
        {
            var r = Build(inputLock: false);
            r.Cec.Lost();
            Assert.False(r.Life.InputsBlocked);
        }
    }
}
