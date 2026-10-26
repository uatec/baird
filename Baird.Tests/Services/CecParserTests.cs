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

        [Theory]
        [InlineData(new byte[] { 0x08, 0x44, 0x01 }, 8, true, (byte)0x01)]
        [InlineData(new byte[] { 0x04, 0x44, 0x01 }, 8, false, (byte)0x01)]
        public void TryParseUserControl_ReadsPressedKeyAndWhetherItIsForUs(byte[] frame, int ourAddress, bool forUs, byte code)
        {
            Assert.True(CecParser.TryParseUserControl(frame, ourAddress, out var userControl));
            Assert.Equal(0, userControl.Source);
            Assert.Equal(frame[0] & 0xF, userControl.Destination);
            Assert.Equal(forUs, userControl.ForThisDevice);
            Assert.Equal(code, userControl.Code);
        }

        [Fact]
        public void TryParseUserControl_ReadsReleaseWithoutACode()
        {
            Assert.True(CecParser.TryParseUserControl(new byte[] { 0x08, 0x45 }, 8, out var userControl));
            Assert.True(userControl.ForThisDevice);
            Assert.Null(userControl.Code);
        }

        [Theory]
        [InlineData(new byte[] { 0x08, 0x90, 0x00 })]
        [InlineData(new byte[] { 0x08, 0x44 })]
        [InlineData(new byte[] { 0x08 })]
        public void TryParseUserControl_RejectsOtherFrames(byte[] frame)
        {
            Assert.False(CecParser.TryParseUserControl(frame, 8, out _));
        }

        [Theory]
        [InlineData(new byte[] { 0x08, 0x41, 0x24 }, (byte)0x44)] // Play forward → Play
        [InlineData(new byte[] { 0x08, 0x41, 0x25 }, (byte)0x46)] // Play still → Pause
        [InlineData(new byte[] { 0x08, 0x41, 0x05 }, (byte)0x49)] // Fast forward
        [InlineData(new byte[] { 0x08, 0x41, 0x09 }, (byte)0x48)] // Fast reverse → Rewind
        [InlineData(new byte[] { 0x08, 0x42, 0x03 }, (byte)0x45)] // Deck Control: Stop
        public void TryParseDeckKey_ReadsTransportKeysAsUserControl(byte[] frame, byte code)
        {
            Assert.True(CecParser.TryParseDeckKey(frame, 8, out var key));
            Assert.True(key.ForThisDevice);
            Assert.Equal(code, key.Code);
        }

        [Theory]
        [InlineData(new byte[] { 0x08, 0x44, 0x01 })]
        [InlineData(new byte[] { 0x08, 0x42, 0x04 })] // Eject
        [InlineData(new byte[] { 0x08, 0x41 })]
        public void TryParseDeckKey_RejectsOtherFrames(byte[] frame)
        {
            Assert.False(CecParser.TryParseDeckKey(frame, 8, out _));
        }
    }
}
