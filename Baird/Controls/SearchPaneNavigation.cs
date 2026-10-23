using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Baird.Controls
{
    /// <summary>
    /// Focus hand-off for the search screens, where the keyboard sits to the left of the results.
    /// Moves inside each pane are left to XY focus; this only handles crossing between panes:
    /// Right off the keyboard's edge goes to the results, Left off the results' first column comes
    /// back to the key last used, and Up from the top row goes to the suggestions or the tab bar.
    /// </summary>
    internal sealed class SearchPaneNavigation
    {
        private readonly VirtualKeyboardControl _keyboard;
        private readonly ItemsControl _results;
        private readonly int _resultColumns;
        private readonly ItemsControl? _suggestions;
        private readonly Action _focusTabBar;
        private Button? _lastKey;

        public SearchPaneNavigation(VirtualKeyboardControl keyboard, ItemsControl results, int resultColumns,
            ItemsControl? suggestions, Action focusTabBar)
        {
            _keyboard = keyboard;
            _results = results;
            _resultColumns = resultColumns;
            _suggestions = suggestions;
            _focusTabBar = focusTabBar;
        }

        /// <summary>Returns true when the key was a pane crossing and has been acted on.</summary>
        public bool Handle(Key key, object? focused)
        {
            if (focused is not Button button) return false;

            if (_keyboard.IsVisualAncestorOf(button))
            {
                _lastKey = button;
                switch (key)
                {
                    case Key.Right when !HasKeyToTheRight(button):
                        // Consume even with no results so XY focus doesn't wander to another key.
                        FocusFirstResult();
                        return true;
                    case Key.Up when Grid.GetRow(button) == 0:
                        return FocusSuggestionsOrTabBar();
                }
            }
            else if (_results.IsVisualAncestorOf(button))
            {
                var index = IndexOfResult(button);
                switch (key)
                {
                    case Key.Left when index >= 0 && index % _resultColumns == 0:
                        return FocusKeyboard();
                    case Key.Up when index >= 0 && index < _resultColumns:
                        return FocusSuggestionsOrTabBar();
                }
            }
            else if (_suggestions != null && _suggestions.IsVisualAncestorOf(button))
            {
                switch (key)
                {
                    case Key.Up:
                        _focusTabBar();
                        return true;
                    case Key.Down:
                        return FocusKeyboard();
                }
            }

            return false;
        }

        public bool FocusKeyboard()
        {
            var key = _lastKey is { IsEffectivelyVisible: true, IsEnabled: true }
                ? _lastKey
                : _keyboard.FindControl<Button>("KeyF");
            return key?.Focus() ?? false;
        }

        public bool FocusFirstResult()
        {
            if (_results.ItemCount == 0) return false;
            var container = _results.ContainerFromIndex(0);
            return container?.FindDescendantOfType<Button>()?.Focus() ?? false;
        }

        private bool FocusSuggestionsOrTabBar()
        {
            var pill = _suggestions?.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible);
            if (pill != null) return pill.Focus();
            _focusTabBar();
            return true;
        }

        private bool HasKeyToTheRight(Button button)
        {
            var row = Grid.GetRow(button);
            var column = Grid.GetColumn(button);
            return _keyboard.GetVisualDescendants().OfType<Button>()
                .Any(k => k != button && k.IsEnabled && Grid.GetRow(k) == row && Grid.GetColumn(k) > column);
        }

        private int IndexOfResult(Button button)
        {
            foreach (var ancestor in button.GetVisualAncestors().OfType<Control>())
            {
                var index = _results.IndexFromContainer(ancestor);
                if (index >= 0) return index;
            }
            return -1;
        }
    }
}
