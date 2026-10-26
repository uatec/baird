using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Baird.NewUi.Diagnostics;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class FreezeWatchdogTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "baird-freeze-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

        private sealed class Ui
        {
            public readonly List<Action> Queued = new();
            public void Post(Action a) => Queued.Add(a);
            public void Run() { foreach (var a in Queued.ToList()) a(); Queued.Clear(); }
        }

        [Fact]
        public void A_ui_thread_that_stops_answering_gets_a_report_naming_what_it_was_doing()
        {
            var t = DateTime.UnixEpoch; var ui = new Ui();
            var crumbs = new Breadcrumbs();
            var dog = new FreezeWatchdog(ui.Post, () => t, _dir, crumbs, () => "screen=Home video=live:BBC One");
            crumbs.Add("key Down");
            using var op = crumbs.Doing("action Ok");     // the UI thread gets stuck inside this
            dog.Check(); ui.Run();                         // answers
            t = t.AddSeconds(2); dog.Check();              // ping not answered yet: not a freeze
            Assert.Empty(Directory.Exists(_dir) ? Directory.GetFiles(_dir) : Array.Empty<string>());
            t = t.AddSeconds(5); dog.Check();              // still nothing: a freeze
            var report = File.ReadAllText(Directory.GetFiles(_dir).Single());
            Assert.Contains("action Ok", report);         // what it was doing
            Assert.Contains("key Down", report);          // what led up to it
            Assert.Contains("screen=Home", report);       // where it was
            t = t.AddSeconds(5); dog.Check();
            Assert.Single(Directory.GetFiles(_dir));       // one report per freeze
        }

        [Fact]
        public void When_it_recovers_the_length_is_recorded_and_the_next_freeze_reports_again()
        {
            var t = DateTime.UnixEpoch; var ui = new Ui();
            var dog = new FreezeWatchdog(ui.Post, () => t, _dir, new Breadcrumbs(), () => "");
            dog.Check();                                   // ping sent; the UI is stuck from here
            t = t.AddSeconds(8); dog.Check();              // reported
            t = t.AddSeconds(4); ui.Run(); dog.Check();    // answers again after 12 s
            Assert.Equal(TimeSpan.FromSeconds(12), dog.LastFreeze);
            t = t.AddSeconds(1); dog.Check();              // a new ping…
            t = t.AddSeconds(10); dog.Check();             // …stuck again: a second report
            Assert.Equal(2, Directory.GetFiles(_dir).Length);
        }

        [Fact]
        public void Only_the_last_reports_are_kept()
        {
            var t = DateTime.UnixEpoch; var ui = new Ui();
            var dog = new FreezeWatchdog(ui.Post, () => t, _dir, new Breadcrumbs(), () => "");
            for (var i = 0; i < FreezeWatchdog.Keep + 3; i++) { dog.Check(); t = t.AddSeconds(10); dog.Check(); t = t.AddSeconds(1); ui.Run(); dog.Check(); t = t.AddSeconds(1); }
            Assert.Equal(FreezeWatchdog.Keep, Directory.GetFiles(_dir).Length);
        }
    }
}
