using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Baird.Controls
{
    /// <summary>
    /// Raises a key that didn't come from the keyboard (e.g. the TV's remote over CEC) the way Avalonia's keyboard
    /// device raises a real one: KeyBindings from the focused element up first — a plain routed event never reaches
    /// them, and channel up/down live there — then KeyDown/KeyUp on the focused element.
    /// </summary>
    public static class SyntheticKeyInput
    {
        public static void Raise(TopLevel topLevel, Key key, bool down)
        {
            var target = topLevel.FocusManager?.GetFocusedElement() as Interactive ?? topLevel;
            var e = new KeyEventArgs
            {
                RoutedEvent = down ? InputElement.KeyDownEvent : InputElement.KeyUpEvent,
                Key = key,
                Source = target,
            };

            if (down)
            {
                for (var visual = target as Visual; visual != null && !e.Handled; visual = visual.GetVisualParent())
                {
                    if (visual is not InputElement element) continue;
                    foreach (var binding in element.KeyBindings.ToArray())
                    {
                        if (binding.Gesture?.Matches(e) != true || binding.Command?.CanExecute(binding.CommandParameter) != true) continue;
                        Console.WriteLine($"[CecRemote] {key} → key binding on {element.GetType().Name}.");
                        binding.Command.Execute(binding.CommandParameter);
                        e.Handled = true;
                        break;
                    }
                }
            }

            target.RaiseEvent(e);
        }
    }
}
