using Avalonia.Controls;
using Baird.NewUi.Core;

namespace Baird.NewUi.Screens
{
    /// <summary>A place, page or overlay. Focus is the screen's own (a lit element), not Avalonia keyboard focus.</summary>
    public interface IScreen
    {
        Control View { get; }
        /// <summary>Handle an action; false lets the shell act (e.g. ▴ off the top lights the tab bar, Back pops).</summary>
        bool OnAction(UiAction action, bool repeat);
        void OnShow() { }
        void OnFocusIn() { }
        void OnFocusOut() { }
        void Destroy() { }
        /// <summary>A text field that a real keyboard types into (letters are then text, not shortcuts).</summary>
        bool AcceptsText => false;
        /// <summary>Text typed on a real keyboard; null deletes the last character (Backspace).</summary>
        void TypeText(string? text) { }
        /// <summary>The lit element, if any (tests check there is exactly one and it is on screen).</summary>
        Control? LitElement => null;
        /// <summary>Where it slides in from as an overlay (the info bar rises; the channel list comes from the left…).</summary>
        (double X, double Y) EnterFrom => (0, 40);
    }
}
