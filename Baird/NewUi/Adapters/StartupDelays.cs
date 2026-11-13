using System;

namespace Baird.NewUi.Adapters
{
    /// <summary>
    /// When the background browser (Netflix, then Disney+ through the same one) is opened after Baird starts. Opening it at 10 s
    /// put its start-up on top of Baird's own: the UI froze for 15-23 s and the Pi ran at load ~30 for two or three minutes.
    /// Late enough for the intro and the home screen to be done and settled; early enough to be ready within a few minutes.
    /// Disney+ opens the browser itself if it is not open, so it must come after Netflix.
    /// </summary>
    public static class StartupDelays
    {
        public static readonly TimeSpan Netflix = TimeSpan.FromSeconds(90);
        public static readonly TimeSpan Disney = TimeSpan.FromSeconds(150);
    }
}
