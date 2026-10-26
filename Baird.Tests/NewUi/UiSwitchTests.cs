using System;
using System.Collections.Generic;
using System.IO;
using Baird.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class IniSettingWriterTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "baird-ini-" + Guid.NewGuid().ToString("N"));
        private string PathOf => System.IO.Path.Combine(_dir, "config.ini");
        public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

        [Fact]
        public void Creates_the_file_and_section()
        {
            IniSettingWriter.Save(PathOf, "Baird", "Ui", "new");
            Assert.Equal(new[] { "[Baird]", "Ui=new" }, File.ReadAllLines(PathOf));
        }

        [Fact]
        public void Updates_an_existing_key_in_place_and_keeps_other_lines()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllLines(PathOf, new[] { "TVH_URL=http://x", "[Baird]", "UiScale=1.2", "Ui=classic", "[Mpv]", "hwdec=no" });
            IniSettingWriter.Save(PathOf, "Baird", "Ui", "new");
            Assert.Equal(new[] { "TVH_URL=http://x", "[Baird]", "UiScale=1.2", "Ui=new", "[Mpv]", "hwdec=no" }, File.ReadAllLines(PathOf));
        }

        [Fact]
        public void Inserts_into_the_section_before_the_next_one()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllLines(PathOf, new[] { "[Baird]", "UiScale=1.2", "[Mpv]", "hwdec=no" });
            IniSettingWriter.Save(PathOf, "Baird", "Ui", "new");
            Assert.Equal(new[] { "[Baird]", "UiScale=1.2", "Ui=new", "[Mpv]", "hwdec=no" }, File.ReadAllLines(PathOf));
        }

        [Fact]
        public void Updates_a_hand_written_key_with_spaces_instead_of_adding_a_duplicate()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllLines(PathOf, new[] { "[Baird]", "; Ui=old comment", "Ui = new", "UiScale=1.4" });
            IniSettingWriter.Save(PathOf, "Baird", "Ui", "classic");
            Assert.Equal(new[] { "[Baird]", "; Ui=old comment", "Ui=classic", "UiScale=1.4" }, File.ReadAllLines(PathOf));
        }

        [Fact]
        public void Does_not_mistake_a_longer_key_for_the_one_being_saved()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllLines(PathOf, new[] { "[Baird]", "UiScale=1.4" });
            IniSettingWriter.Save(PathOf, "Baird", "Ui", "new");
            Assert.Equal(new[] { "[Baird]", "UiScale=1.4", "Ui=new" }, File.ReadAllLines(PathOf));
        }

        [Fact]
        public void Appends_to_the_section_when_it_is_last()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllLines(PathOf, new[] { "X=1", "[Baird]", "UiScale=1.2" });
            IniSettingWriter.Save(PathOf, "Baird", "Ui", "new");
            Assert.Equal(new[] { "X=1", "[Baird]", "UiScale=1.2", "Ui=new" }, File.ReadAllLines(PathOf));
        }
    }

    public class UiModeTests
    {
        private static IConfiguration Config(params (string, string)[] kv)
        {
            var d = new Dictionary<string, string?>();
            foreach (var (k, v) in kv) d[k] = v;
            return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
        }

        [Fact]
        public void A_new_ui_that_fails_to_start_falls_back_to_classic()
        {
            var built = UiModes.Build<string>(UiMode.New, () => throw new InvalidOperationException("font missing"), () => "classic");
            Assert.Equal("classic", built);
        }

        [Fact] public void Classic_mode_builds_classic() => Assert.Equal("classic", UiModes.Build(UiMode.Classic, () => "new", () => "classic"));
        [Fact] public void New_mode_builds_new() => Assert.Equal("new", UiModes.Build(UiMode.New, () => "new", () => "classic"));

        [Fact] public void Missing_is_classic() => Assert.Equal(UiMode.Classic, UiModes.Read(Config()));
        [Fact] public void New_from_the_setting() => Assert.Equal(UiMode.New, UiModes.Read(Config(("Baird:Ui", "new"))));
        [Fact] public void Case_insensitive() => Assert.Equal(UiMode.New, UiModes.Read(Config(("Baird:Ui", "NEW"))));
        [Fact] public void Unknown_is_classic() => Assert.Equal(UiMode.Classic, UiModes.Read(Config(("Baird:Ui", "fancy"))));
        [Fact] public void Environment_override_wins() => Assert.Equal(UiMode.New, UiModes.Read(Config(("Baird:Ui", "classic"), ("BAIRD_UI", "new"))));
        [Fact] public void Text_round_trips() { Assert.Equal("new", UiModes.ToSetting(UiMode.New)); Assert.Equal("classic", UiModes.ToSetting(UiMode.Classic)); }
    }

    public class AppRestarterTests
    {
        private sealed class Runner : IProcessRunner
        {
            public readonly List<(string File, string Args)> Started = new();
            public void Start(string file, string args) => Started.Add((file, args));
        }

        [Fact]
        public void Under_systemd_it_asks_systemd_to_restart_the_service_and_does_not_exit_itself()
        {
            var r = new Runner(); var exited = false;
            new AppRestarter(r, name => name == "INVOCATION_ID" ? "abc" : null, () => exited = true, "/opt/baird/Baird", new string[0]).Restart();
            Assert.Single(r.Started);
            Assert.Equal(("systemctl", "--user restart baird.service"), r.Started[0]);
            Assert.False(exited);
        }

        [Fact]
        public void Run_by_hand_it_starts_a_fresh_copy_with_the_same_arguments_then_exits()
        {
            var r = new Runner(); var exited = false;
            new AppRestarter(r, _ => null, () => exited = true, "/home/u/Baird", new[] { "--foo", "a b" }).Restart();
            Assert.Equal(("/home/u/Baird", "--foo \"a b\""), r.Started[0]);
            Assert.True(exited);
        }
    }
}

namespace Baird.Tests.NewUi
{
    public class ClassicSettingsInterfaceToggleTests
    {
        [Avalonia.Headless.XUnit.AvaloniaFact]
        public void Toggling_saves_new_to_the_config_and_restarts()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "baird-cfg-" + System.Guid.NewGuid().ToString("N"), "config.ini");
            var restarted = 0;
            var vm = new Baird.ViewModels.SettingsViewModel(null) { ConfigPath = path, Restart = () => restarted++ };
            Assert.Equal("Classic", vm.UiModeText);
            vm.ToggleUiModeCommand.Execute().Subscribe();
            Assert.Contains("Ui=new", System.IO.File.ReadAllLines(path));
            Assert.Equal(1, restarted);
            Assert.Equal("New", vm.UiModeText);
        }

        [Avalonia.Headless.XUnit.AvaloniaFact]
        public void A_second_press_while_restarting_does_nothing()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "baird-cfg-" + System.Guid.NewGuid().ToString("N"), "config.ini");
            var restarted = 0;
            var vm = new Baird.ViewModels.SettingsViewModel(null) { ConfigPath = path, Restart = () => restarted++ };
            vm.ToggleUiModeCommand.Execute().Subscribe();
            vm.ToggleUiModeCommand.Execute().Subscribe();
            Assert.Contains("Ui=new", System.IO.File.ReadAllLines(path));
            Assert.Equal(1, restarted);
        }
    }
}
