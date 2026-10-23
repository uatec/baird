using System.Text.Json;
using Baird.Services;
using Xunit;

namespace Baird.Tests.Services
{
    public class CdpPageSignalTests
    {
        private static JsonElement Message(string json) => JsonDocument.Parse(json).RootElement;

        [Fact]
        public void BindingCall_ForBairdSignal_ReturnsPayload()
        {
            var m = Message("{\"method\":\"Runtime.bindingCalled\",\"params\":{\"name\":\"bairdSignal\",\"payload\":\"{\\\"type\\\":\\\"exit\\\"}\",\"executionContextId\":3}}");

            Assert.True(CdpBrowserService.TryReadPageSignal(m, out var payload));
            Assert.Equal("{\"type\":\"exit\"}", payload);
        }

        [Theory]
        [InlineData("{\"method\":\"Runtime.bindingCalled\",\"params\":{\"name\":\"somethingElse\",\"payload\":\"x\"}}")]
        [InlineData("{\"method\":\"Network.requestWillBeSent\",\"params\":{}}")]
        [InlineData("{\"method\":\"Runtime.bindingCalled\"}")]
        public void OtherMessages_AreNotSignals(string json)
        {
            Assert.False(CdpBrowserService.TryReadPageSignal(Message(json), out _));
        }
    }
}
