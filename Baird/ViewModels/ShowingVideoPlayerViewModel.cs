using ReactiveUI;

namespace Baird.ViewModels
{
    /// <summary>
    /// ViewModel that represents the state when the video player is the active view.
    /// This ViewModel is pushed to the navigation stack to allow proper back navigation,
    /// but does not render any UI (the video player is always rendered as the base layer).
    /// </summary>
    public class ShowingVideoPlayerViewModel : ReactiveObject
    {
        // This ViewModel intentionally has no properties or behavior.
        // It exists purely as a navigation stack entry to represent the "showing video player" state.
    }
}
