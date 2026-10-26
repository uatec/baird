using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Input;
using Baird.Services;
using Baird.Tests.Mocks;
using Xunit;

namespace Baird.Tests.Services
{
    public class CecRemoteControlTests
    {
        private const int Baird = 8;

        private static CecUserControlEventArgs Press(byte code, int destination = Baird) =>
            new() { Source = 0, Destination = destination, ForThisDevice = destination == Baird, Code = code };

        private static CecUserControlEventArgs Released(int destination = Baird) =>
            new() { Source = 0, Destination = destination, ForThisDevice = destination == Baird, Code = null };

        private static (MockCecService Cec, List<(Key Key, bool Down)> Keys, CecRemoteControl Remote) Create(TimeSpan? releaseTimeout = null)
        {
            var cec = new MockCecService();
            var keys = new List<(Key, bool)>();
            var remote = new CecRemoteControl(cec, (key, down) => { lock (keys) keys.Add((key, down)); }, releaseTimeout ?? TimeSpan.FromSeconds(30));
            return (cec, keys, remote);
        }

        [Theory]
        [InlineData(0x00, Key.Enter)]
        [InlineData(0x01, Key.Up)]
        [InlineData(0x02, Key.Down)]
        [InlineData(0x03, Key.Left)]
        [InlineData(0x04, Key.Right)]
        [InlineData(0x0D, Key.BrowserBack)]
        [InlineData(0x20, Key.D0)]
        [InlineData(0x25, Key.D5)]
        [InlineData(0x29, Key.D9)]
        [InlineData(0x30, Key.PageDown)] // swapped: see CecRemoteControl
        [InlineData(0x31, Key.PageUp)]
        [InlineData(0x53, Key.G)]
        public void TvRemoteKeys_MapToTheKeysBairdHandles(byte code, Key expected)
        {
            Assert.True(CecRemoteControl.TryMapKey(code, out var key));
            Assert.Equal(expected, key);
        }

        [Theory]
        [InlineData(0x40)] // Power
        [InlineData(0x41)] // Volume Up
        [InlineData(0x42)] // Volume Down
        [InlineData(0x43)] // Mute
        public void PowerAndVolume_AreLeftToTheTv(byte code)
        {
            // Mapping these would send them straight back to the TV as CEC commands.
            Assert.False(CecRemoteControl.TryMapKey(code, out _));
        }

        [Fact]
        public void PressThenRelease_RaisesKeyDownThenKeyUp()
        {
            var (cec, keys, _) = Create();
            cec.SimulateUserControl(Press(0x01));
            cec.SimulateUserControl(Released());

            Assert.Equal(new[] { (Key.Up, true), (Key.Up, false) }, keys);
        }

        [Fact]
        public void KeysForAnotherDevice_AreIgnored()
        {
            var (cec, keys, _) = Create();
            cec.SimulateUserControl(Press(0x01, destination: 4));
            cec.SimulateUserControl(Released(destination: 4));

            Assert.Empty(keys);
        }

        [Fact]
        public void HeldKey_RepeatsKeyDownAndReleasesOnce()
        {
            var (cec, keys, _) = Create();
            cec.SimulateUserControl(Press(0x02));
            cec.SimulateUserControl(Press(0x02));
            cec.SimulateUserControl(Press(0x02));
            cec.SimulateUserControl(Released());
            cec.SimulateUserControl(Released());

            Assert.Equal(new[] { (Key.Down, true), (Key.Down, true), (Key.Down, true), (Key.Down, false) }, keys);
        }

        [Fact]
        public void NewKeyBeforeRelease_ReleasesThePreviousKeyFirst()
        {
            var (cec, keys, _) = Create();
            cec.SimulateUserControl(Press(0x21));
            cec.SimulateUserControl(Press(0x22));

            Assert.Equal(new[] { (Key.D1, true), (Key.D1, false), (Key.D2, true) }, keys);
        }

        [Fact]
        public void UnmappedKey_RaisesNothing()
        {
            var (cec, keys, _) = Create();
            cec.SimulateUserControl(Press(0x72)); // Red
            Assert.Empty(keys);
        }

        [Fact]
        public async Task MissingRelease_IsAssumedAfterTheTimeout()
        {
            var (cec, keys, _) = Create(TimeSpan.FromMilliseconds(50));
            cec.SimulateUserControl(Press(0x00));

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                lock (keys) if (keys.Count == 2) break;
                await Task.Delay(10);
            }

            lock (keys) Assert.Equal(new[] { (Key.Enter, true), (Key.Enter, false) }, keys);
        }

        [Fact]
        public void Dispose_StopsListening()
        {
            var (cec, keys, remote) = Create();
            remote.Dispose();
            cec.SimulateUserControl(Press(0x01));
            Assert.Empty(keys);
        }
    }
}
