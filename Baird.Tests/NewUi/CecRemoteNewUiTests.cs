using Avalonia.Input;
using Baird.NewUi.Core;
using Baird.Services;
using Xunit;

namespace Baird.Tests.NewUi
{
    /// <summary>The TV's remote over CEC must land on the new UI's actions, not just on keys.</summary>
    public class CecRemoteNewUiTests
    {
        [Theory]
        [InlineData(0x00, UiAction.Ok)]
        [InlineData(0x01, UiAction.Up)]
        [InlineData(0x02, UiAction.Down)]
        [InlineData(0x03, UiAction.Left)]
        [InlineData(0x04, UiAction.Right)]
        [InlineData(0x09, UiAction.Home)]
        [InlineData(0x0D, UiAction.Back)]
        [InlineData(0x20, UiAction.Digit0)]
        [InlineData(0x27, UiAction.Digit7)]
        [InlineData(0x2B, UiAction.Ok)]
        [InlineData(0x30, UiAction.PageDown)]
        [InlineData(0x31, UiAction.PageUp)]
        [InlineData(0x44, UiAction.PlayPause)]
        [InlineData(0x46, UiAction.PlayPause)]
        [InlineData(0x53, UiAction.Guide)]
        [InlineData(0x61, UiAction.PlayPause)]
        public void TvRemoteKey_MapsToTheNewUiAction(byte code, UiAction expected)
        {
            Assert.True(CecRemoteControl.TryMapKey(code, out var key));
            Assert.Equal(expected, new InputMap().Map(key));
        }

        [Theory]
        [InlineData(Key.A, "a", "a")]
        [InlineData(Key.A, "A", "A")]
        [InlineData(Key.Space, " ", " ")]
        [InlineData(Key.H, "h", "h")]
        [InlineData(Key.D1, "1", "1")]
        public void A_printable_key_is_typed_text(Key key, string symbol, string expected)
        {
            Assert.True(InputMap.TryText(key, KeyModifiers.None, symbol, out var text));
            Assert.Equal(expected, text);
        }

        [Fact]
        public void Backspace_deletes_and_remote_and_control_keys_are_not_text()
        {
            Assert.True(InputMap.TryText(Key.Back, KeyModifiers.None, "\b", out var del));
            Assert.Null(del);
            Assert.False(InputMap.TryText(Key.Enter, KeyModifiers.None, "\r", out _));
            Assert.False(InputMap.TryText(Key.Escape, KeyModifiers.None, "\u001b", out _));
            Assert.False(InputMap.TryText(Key.Up, KeyModifiers.None, null, out _));
            Assert.False(InputMap.TryText(Key.BrowserBack, KeyModifiers.None, null, out _));
            Assert.False(InputMap.TryText(Key.C, KeyModifiers.Control, "c", out _));
        }
    }
}
