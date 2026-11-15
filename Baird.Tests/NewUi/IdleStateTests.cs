using System;
using System.IO;
using Baird.NewUi.Lifecycle;
using Baird.NewUi.Screens;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class IdleStateTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "baird-state-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

        [Fact]
        public void The_state_file_holds_one_word_for_the_updater_to_read()
        {
            var path = Path.Combine(_dir, "sub", "state");   // the folder may not exist yet
            var state = new FileIdleState(path);
            state.Set(false);
            Assert.Equal("active\n", File.ReadAllText(path));
            state.Set(true);
            Assert.Equal("idle\n", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"));
        }

        [Fact]
        public void An_unwritable_location_does_not_throw()
        {
            File.WriteAllText(_dir, "");   // a file where the folder should be
            new FileIdleState(Path.Combine(_dir, "state")).Set(true);
        }

        [Fact]
        public void The_still_watching_prompt_starts_hidden_and_asks_the_question()
        {
            var prompt = new StillWatchingPrompt();
            Assert.False(prompt.IsShowing);
            prompt.Show();
            Assert.True(prompt.IsShowing);
            Assert.Contains("Still watching?", prompt.Text);
            Assert.Contains("any button", prompt.Text);
            prompt.Hide();
            Assert.False(prompt.IsShowing);
        }
    }
}
