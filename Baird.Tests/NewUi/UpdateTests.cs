using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Baird.NewUi.Core;
using Baird.NewUi.Screens;
using Baird.Services;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class UpdateTests
    {
        private sealed class Runner : IProcessRunner
        {
            public readonly List<(string File, string Args)> Started = new();
            public void Start(string file, string args) => Started.Add((file, args));
        }

        private static UpdateService Service(string? latest, Runner runner, bool underSystemd = true, string current = "v0.0.253") =>
            new(() => Task.FromResult(latest), runner, k => k == "INVOCATION_ID" && underSystemd ? "abc" : null, "/opt/baird/libexec/update_baird.sh", current);

        [Theory]
        [InlineData("v0.0.255", "v0.0.253", true)]
        [InlineData("v0.0.253", "v0.0.253", false)]
        [InlineData("v0.0.100", "v0.0.253", false)]
        [InlineData("v0.1.0", "v0.0.999", true)]
        [InlineData(null, "v0.0.253", false)]
        public void A_release_is_newer_only_if_its_version_is_higher(string? latest, string current, bool newer) =>
            Assert.Equal(newer, UpdateService.IsNewer(latest, current));

        [Fact]
        public void Updating_runs_the_updater_detached_so_it_survives_baird_restarting()
        {
            var runner = new Runner();
            Service("v0.0.255", runner).StartUpdate();
            var (file, args) = runner.Started.Single();
            Assert.Equal("systemd-run", file);
            Assert.Contains("--user", args);
            Assert.Contains("FORCE=1", args);
            Assert.Contains("/opt/baird/libexec/update_baird.sh", args);
        }

        private static async Task Settle() { for (var i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); } }

        private static SettingsScreen Settings(UpdateService updates) => new(() => UiMode.New, _ => { }, 1.0, _ => { }, updates: updates);

        [AvaloniaFact]
        public async Task Settings_shows_this_version_and_the_newer_one_and_ok_updates()
        {
            var runner = new Runner();
            var s = Settings(Service("v0.0.255", runner));
            await Settle();
            Assert.Contains("v0.0.253", s.UpdateText);
            Assert.Contains("v0.0.255", s.UpdateText);
            s.OnAction(UiAction.Down, false); s.OnAction(UiAction.Down, false);
            s.OnAction(UiAction.Ok, false);
            Assert.Single(runner.Started);
            Assert.Contains("Updating to v0.0.255", s.UpdateText);
            s.OnAction(UiAction.Ok, false);
            Assert.Single(runner.Started); // once
        }

        [AvaloniaFact]
        public async Task When_up_to_date_ok_checks_again_and_doesnt_update()
        {
            var runner = new Runner();
            var s = Settings(Service("v0.0.253", runner));
            await Settle();
            Assert.Contains("up to date", s.UpdateText);
            s.OnAction(UiAction.Down, false); s.OnAction(UiAction.Down, false);
            s.OnAction(UiAction.Ok, false);
            await Settle();
            Assert.Empty(runner.Started);
        }

        [AvaloniaFact]
        public async Task If_github_cant_be_reached_it_says_so()
        {
            var s = Settings(Service(null, new Runner()));
            await Settle();
            Assert.Contains("couldn't check", s.UpdateText);
        }
    }
}
