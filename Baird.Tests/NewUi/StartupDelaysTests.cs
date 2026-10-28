using System;
using Baird.NewUi.Adapters;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class StartupDelaysTests
    {
        [Fact]
        public void The_background_browser_waits_for_Baird_to_settle_and_Disney_comes_after_Netflix()
        {
            // Disney+ opens the browser itself if it is closed, so an earlier Disney+ would bring the start-up storm back.
            Assert.True(StartupDelays.Netflix >= TimeSpan.FromSeconds(60));
            Assert.True(StartupDelays.Disney > StartupDelays.Netflix);
        }
    }
}
