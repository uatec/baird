using System.Collections.Generic;
using Baird.Services;
using Xunit;

namespace Baird.Tests.Services
{
    public class CecParserTests
    {
        [Theory]
        [InlineData("TRAFFIC: [123] >> 01:44:00", "Recording 1 -> TV: User Control Pressed: Select")]
        [InlineData(">> 0f:36", "TV -> Broadcast: Standby")]
        [InlineData(">> 05:90:00", "TV -> Audio System: Report Power Status: On")]
        [InlineData(">> 05:90:01", "TV -> Audio System: Report Power Status: Standby")]
        [InlineData(">> 10:82:11:00", "Recording 1 -> TV: Active Source: 11.00.0.0")]
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
    }
}
