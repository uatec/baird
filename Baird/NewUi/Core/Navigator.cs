using System;
using System.Collections.Generic;
using System.Linq;

namespace Baird.NewUi.Core
{
    /// <summary>The tabs.</summary>
    public enum Place { Home, LiveTv, Search, Settings }

    /// <summary>A page. <paramref name="Title"/> is what Back chips call it ("Back to The Simpsons"), else its name.</summary>
    public sealed record PageRef(string Name, object? Params = null, string? Title = null)
    {
        public string Label => Title ?? Name;
    }
    public sealed record OverlayRef(string Name, object? Params = null);
    public sealed record VideoRef(string Kind, object Ref);

    public sealed record NavState(VideoRef? Video, bool AtVideo, IReadOnlyList<PageRef> Pages, OverlayRef? Overlay,
        IReadOnlyList<PageRef> OriginPages, Place Place);

    /// <summary>
    /// The spatial model, ported from the prototype (spikes/ux-redesign-prototype/js/nav.js): the video is the ground
    /// floor, a place (tab) is the bottom page, pages stack above it (or above the video), and one overlay can sit on top.
    /// Back retraces the journey: overlay → closes; page → pops; bare video → the pages it was started from;
    /// a place other than Home → Home.
    /// </summary>
    public sealed class Navigator
    {
        private VideoRef? _video;
        private bool _atVideo;
        private List<PageRef> _pages = new();
        private OverlayRef? _overlay;
        private List<PageRef> _originPages = new();
        private Place _place = Place.Home;

        public event Action? Changed;
        private void Raise() => Changed?.Invoke();

        public NavState State => new(_video, _atVideo, _pages.ToList(), _overlay, _originPages.ToList(), _place);

        /// <summary>The overlay, else the top page, else "video" when the video is the ground, else the place.</summary>
        public object Top => (object?)_overlay ?? (object?)_pages.LastOrDefault() ?? (_video != null && _atVideo ? "video" : _place);

        public void SetPlace(Place place)
        {
            if (_place == place && _pages.Count == 0 && _overlay == null && !_atVideo) return;
            _place = place; _pages = new(); _overlay = null; _atVideo = false; _originPages = new();
            Raise();
        }

        /// <summary>
        /// Straight to a place's bare content from anywhere (≡, voice, "This channel's schedule"): the stack, overlay and
        /// journey back are cleared and the video (if any) stays behind it — in one change, so nothing flashes between.
        /// </summary>
        public void GoPlace(Place place)
        {
            if (_place == place && _pages.Count == 0 && _overlay == null && !_atVideo && _originPages.Count == 0) return;
            _place = place; _pages = new(); _overlay = null; _atVideo = false; _originPages = new();
            Raise();
        }

        public void OpenPage(PageRef page) { _overlay = null; _pages.Add(page); Raise(); }
        public void OpenOverlay(OverlayRef overlay) { _overlay = overlay; Raise(); }
        public void CloseOverlay() { if (_overlay == null) return; _overlay = null; Raise(); }

        public void Play(VideoRef video, bool keepOverlay = false)
        {
            if (_pages.Count > 0) { _originPages = _pages; _pages = new(); }
            else if (!_atVideo) _originPages = new();
            if (!keepOverlay) _overlay = null;
            _video = video;
            _atVideo = true;
            Raise();
        }

        public void Stop()
        {
            _video = null; _atVideo = false; _overlay = null;
            _pages = _originPages.Concat(_pages).ToList(); _originPages = new();
            Raise();
        }

        public void Back()
        {
            if (_overlay != null) _overlay = null;
            else if (_pages.Count > 0) _pages.RemoveAt(_pages.Count - 1);
            else if (_video != null && _atVideo) { _atVideo = false; _pages = _originPages; _originPages = new(); }
            else if (_place != Place.Home) _place = Place.Home;
            else return;
            Raise();
        }

        /// <summary>Return to the bare video (dismissing pages opened over it).</summary>
        public void ToVideo()
        {
            if (_video == null) return;
            _overlay = null;
            if (_atVideo) _pages = new();
            else { _atVideo = true; _originPages = _pages; _pages = new(); }
            Raise();
        }

        public void Home()
        {
            _place = Place.Home; _overlay = null; _pages = new(); _originPages = new(); _atVideo = false;
            Raise();
        }
    }
}
