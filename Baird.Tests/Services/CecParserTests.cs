using System.Collections.Generic;
using Baird.Services;
using Xunit;

namespace Baird.Tests.Services
{
    public class CecParserTests
    {
        [Theory]
        [InlineData("TRAFFIC: [123] >> 01:44:00", "TV (0) -> Recording 1 (1): User Control Pressed: Select")]
        [InlineData(">> 0f:36", "TV (0) -> Broadcast (F): Standby")]
        [InlineData(">> 05:90:00", "TV (0) -> Audio System (5): Report Power Status: On")]
        [InlineData(">> 05:90:01", "TV (0) -> Audio System (5): Report Power Status: Standby")]
        [InlineData(">> 10:82:11:00", "Recording 1 (1) -> TV (0): Active Source: 11.00.0.0")]
        [InlineData("TRAFFIC: [581] << 88", "Playback 2 (8) -> Playback 2 (8): POLL")]
        public void ParseLine_ShouldInterpretCorrectly(string input, string expectedInterpretationEnd)
        {
            var result = CecParser.ParseLine(input);
            Assert.Contains(expectedInterpretationEnd, result);
        }

        [Fact]
        public void ParseLine_ShouldHandleUnparseableLines()
        {
            var line = "DEBUG: some debug info";
            var result = CecParser.ParseLine(line);
            Assert.Equal(line, result);
        }

        [Fact]
        public void ParseLine_ShouldHandleUnknownOpcodes()
        {
            var line = ">> 05:FF:00"; // FF is unknown opcode
            var result = CecParser.ParseLine(line);
            Assert.Contains("Opcode FF", result);
        }

        [Theory]
        [InlineData("TRAFFIC: [  4210]\t>> 08:90:00", true, new byte[] { 0x08, 0x90, 0x00 })]
        [InlineData("TRAFFIC: [  4211]\t<< 8f:82:30:00", false, new byte[] { 0x8F, 0x82, 0x30, 0x00 })]
        [InlineData("<< 88", false, new byte[] { 0x88 })]
        public void TryParseFrame_ExtractsDirectionAndBytes(string line, bool expectedIncoming, byte[] expectedFrame)
        {
            Assert.True(CecParser.TryParseFrame(line, out var incoming, out var frame));
            Assert.Equal(expectedIncoming, incoming);
            Assert.Equal(expectedFrame, frame);
        }

        [Theory]
        [InlineData("DEBUG: some debug info")]
        [InlineData("")]
        public void TryParseFrame_RejectsNonTrafficLines(string line)
        {
            Assert.False(CecParser.TryParseFrame(line, out _, out _));
        }
    }
}
