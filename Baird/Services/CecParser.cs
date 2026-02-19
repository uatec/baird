using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Baird.Services
{
    public static class CecParser
    {
        private static readonly Dictionary<int, string> LogicalAddresses = new()
        {
            { 0, "TV" },
            { 1, "Recording 1" },
            { 2, "Recording 2" },
            { 3, "Tuner 1" },
            { 4, "Playback 1" },
            { 5, "Audio System" },
            { 6, "Tuner 2" },
            { 7, "Tuner 3" },
            { 8, "Playback 2" },
            { 9, "Recording 3" },
            { 10, "Tuner 4" },
            { 11, "Playback 3" },
            { 12, "Reserved 1" },
            { 13, "Reserved 2" },
            { 14, "Free Use" },
            { 15, "Broadcast" }
        };

        private static readonly Dictionary<int, string> Opcodes = new()
        {
            { 0x04, "Image View On" },
            { 0x0D, "Text View On" },
            { 0x32, "Set Menu Language" },
            { 0x36, "Standby" },
            { 0x44, "User Control Pressed" },
            { 0x45, "User Control Released" },
            { 0x46, "Give OSD Name" },
            { 0x47, "Set OSD Name" },
            { 0x71, "Give Audio Status" },
            { 0x72, "Set System Audio Mode" },
            { 0x7A, "Report Audio Status" },
            { 0x80, "Routing Change" },
            { 0x81, "Routing Information" },
            { 0x82, "Active Source" },
            { 0x83, "Give Physical Address" },
            { 0x84, "Report Physical Address" },
            { 0x85, "Request Active Source" },
            { 0x86, "Set Stream Path" },
            { 0x87, "Device Vendor ID" },
            { 0x89, "Vendor Command" },
            { 0x8C, "Give Device Vendor ID" },
            { 0x8D, "Menu Request" },
            { 0x8E, "Menu Status" },
            { 0x8F, "Give Device Power Status" },
            { 0x90, "Report Power Status" },
            { 0x91, "Get Menu Language" },
            { 0x9D, "Inactive Source" },
            { 0x9E, "CEC Version" },
            { 0x9F, "Get CEC Version" },
            { 0xA0, "Vendor Command With ID" },
            { 0xC0, "Initiate ARC" },
            { 0xC1, "Report ARC Initiated" },
            { 0xC2, "Report ARC Terminated" },
            { 0xC3, "Request ARC Initiation" },
            { 0xC4, "Request ARC Termination" },
            { 0xC5, "Terminate ARC" }
        };

        private static readonly Dictionary<int, string> UserControlCodes = new()
        {
            { 0x00, "Select" },
            { 0x01, "Up" },
            { 0x02, "Down" },
            { 0x03, "Left" },
            { 0x04, "Right" },
            { 0x05, "Right-Up" },
            { 0x06, "Right-Down" },
            { 0x07, "Left-Up" },
            { 0x08, "Left-Down" },
            { 0x09, "Root Menu" },
            { 0x0A, "Setup Menu" },
            { 0x0B, "Contents Menu" },
            { 0x0C, "Favorite Menu" },
            { 0x0D, "Exit" },
            { 0x20, "0" },
            { 0x21, "1" },
            { 0x22, "2" },
            { 0x23, "3" },
            { 0x24, "4" },
            { 0x25, "5" },
            { 0x26, "6" },
            { 0x27, "7" },
            { 0x28, "8" },
            { 0x29, "9" },
            { 0x2A, "Dot" },
            { 0x2B, "Enter" },
            { 0x2C, "Clear" },
            { 0x2F, "Next Favorite" },
            { 0x30, "Channel Up" },
            { 0x31, "Channel Down" },
            { 0x32, "Previous Channel" },
            { 0x33, "Sound Select" },
            { 0x34, "Input Select" },
            { 0x35, "Show Info" },
            { 0x36, "Help" },
            { 0x37, "Page Up" },
            { 0x38, "Page Down" },
            { 0x40, "Power" },
            { 0x41, "Volume Up" },
            { 0x42, "Volume Down" },
            { 0x43, "Mute" },
            { 0x44, "Play" },
            { 0x45, "Stop" },
            { 0x46, "Pause" },
            { 0x47, "Record" },
            { 0x48, "Rewind" },
            { 0x49, "Fast Forward" },
            { 0x4A, "Eject" },
            { 0x4B, "Forward" },
            { 0x4C, "Backward" },
            { 0x50, "Angle" },
            { 0x51, "Subpicture" },
            { 0x52, "Video on Demand" },
            { 0x53, "EPG" },
            { 0x54, "Timer Programming" },
            { 0x55, "Initial Configuration" },
            { 0x60, "Play Function" },
            { 0x61, "Pause-Play Function" },
            { 0x62, "Record Function" },
            { 0x63, "Pause-Record Function" },
            { 0x64, "Stop Function" },
            { 0x65, "Mute Function" },
            { 0x66, "Restore Volume Function" },
            { 0x67, "Tune Function" },
            { 0x68, "Select Media Function" },
            { 0x69, "Select A/V Input Function" },
            { 0x6A, "Select Audio Input Function" },
            { 0x6B, "Power Toggle Function" },
            { 0x6C, "Power Off Function" },
            { 0x6D, "Power On Function" },
            { 0x71, "Blue" },
            { 0x72, "Red" },
            { 0x73, "Green" },
            { 0x74, "Yellow" },
            { 0x75, "F5" },
            { 0x76, "Data" },
            { 0x91, "Active Source" }
        };

        private static string GetAddressName(int address) =>
            LogicalAddresses.TryGetValue(address, out var name) ? $"{name} ({address:X})" : $"Unknown ({address:X})";

        public static string ParseLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return line;

            // Handle "TRAFFIC: [timestamp] >> xx:xx:xx..."
            // We want to extract the hex part
            var trafficMatch = Regex.Match(line, @"(?:TRAFFIC:.*)?(?:>>|<<)\s+([0-9a-fA-F]{2}(?::[0-9a-fA-F]{2})*)");
            if (trafficMatch.Success)
            {
                var hexString = trafficMatch.Groups[1].Value;
                // If it's just a single byte (POLL), handling might be slightly different but logic should hold
                if (!hexString.Contains(':') && hexString.Length == 2)
                {
                    // It's a POLL or single byte
                    return $"{line}  [{ParsePacket(hexString)}]";
                }

                return $"{line}  [{ParsePacket(hexString)}]";
            }

            // Try matching raw hex string if the whole line is traffic
            if (Regex.IsMatch(line.Trim(), @"^[0-9a-fA-F]{2}(?::[0-9a-fA-F]{2})*$"))
            {
                return $"{line}  [{ParsePacket(line.Trim())}]";
            }

            return line;
        }

        private static string ParsePacket(string hexString)
        {
            try
            {
                var parts = hexString.Split(':');
                if (parts.Length == 0 || string.IsNullOrWhiteSpace(hexString)) return "Empty Packet";

                var bytes = parts.Select(b => Convert.ToByte(b, 16)).ToArray();
                if (bytes.Length == 0) return "Empty Packet";

                var src = (bytes[0] >> 4) & 0xF;
                var dst = bytes[0] & 0xF;

                var srcName = GetAddressName(src);
                var dstName = GetAddressName(dst);

                // Single byte is just a POLL message
                if (bytes.Length == 1)
                {
                    return $"{srcName} -> {dstName}: POLL";
                }

                var opcode = bytes[1];

                string description;
                if (Opcodes.TryGetValue(opcode, out var opcodeName))
                {
                    description = opcodeName;

                    // Specific parsing for some opcodes
                    if (opcode == 0x44 && bytes.Length > 2) // User Control Pressed
                    {
                        var key = bytes[2];
                        if (UserControlCodes.TryGetValue(key, out var keyName))
                        {
                            description += $": {keyName}";
                        }
                        else
                        {
                            description += $": Unknown Key ({key:X2})";
                        }
                    }
                    else if (opcode == 0x90 && bytes.Length > 2) // Report Power Status
                    {
                        var status = bytes[2];
                        description += $": {(status == 0x00 ? "On" : status == 0x01 ? "Standby" : $"Status {status:X2}")}";
                    }
                    else if (opcode == 0x82 && bytes.Length > 3) // Active Source
                    {
                        description += $": {bytes[2]:X2}.{bytes[3]:X2}.0.0"; // Approx physical address format
                    }
                }
                else
                {
                    description = $"Opcode {opcode:X2}";
                }

                return $"{srcName} -> {dstName}: {description}";
            }
            catch
            {
                return "Parse Error";
            }
        }
    }
}
