using System;
using Baird.Services;
using Xunit;

namespace Baird.Tests.Services
{
    public class RemoteKeyGateTests
    {
        private static readonly DateTime T0 = new(2026, 10, 21, 22, 0, 0);
        private static DateTime At(double ms) => T0.AddMilliseconds(ms);

        [Fact]
        public void HeldKey_IsThrottledToTheRepeatInterval()
        {
            var gate = new RemoteKeyGate(TimeSpan.FromMilliseconds(300));
            var sent = 0;
            // 600 ms autorepeat delay, then 25 repeats/s for 2 s.
            if (gate.ShouldSend(At(0))) sent++;
            for (var ms = 600.0; ms <= 2600; ms += 40)
                if (gate.ShouldSend(At(ms))) sent++;

            Assert.InRange(sent, 6, 8);
        }

        [Fact]
        public void StuckKey_StopsSendingAfterMaxHold()
        {
            var gate = new RemoteKeyGate(TimeSpan.FromMilliseconds(300));
            for (var ms = 0.0; ms <= RemoteKeyGate.MaxHold.TotalMilliseconds; ms += 40)
                gate.ShouldSend(At(ms));

            for (var ms = RemoteKeyGate.MaxHold.TotalMilliseconds + 40; ms < 120_000; ms += 40)
                Assert.False(gate.ShouldSend(At(ms)));
        }

        [Fact]
        public void PressAfterRelease_SendsAgain()
        {
            var gate = new RemoteKeyGate(TimeSpan.FromMilliseconds(300));
            for (var ms = 0.0; ms <= 10_000; ms += 40)
                gate.ShouldSend(At(ms));

            Assert.True(gate.ShouldSend(At(12_000)));
        }

        [Fact]
        public void NoRepeatGate_SendsOncePerPress()
        {
            var gate = new RemoteKeyGate(TimeSpan.MaxValue);
            Assert.True(gate.ShouldSend(At(0)));
            for (var ms = 600.0; ms <= 3000; ms += 40)
                Assert.False(gate.ShouldSend(At(ms)));

            Assert.True(gate.ShouldSend(At(5000)));
        }
    }
}
