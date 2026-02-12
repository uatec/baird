using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Baird.Models
{
    public class ScreensaverResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("data")]
        public List<ScreensaverCollection>? Data { get; set; }
    }

    public class ScreensaverCollection
    {
        [JsonPropertyName("screensavers")]
        public List<ScreensaverAsset>? Screensavers { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; } // e.g., "Africa Night"
    }

    public class ScreensaverAsset
    {
        [JsonPropertyName("identifier")]
        public string? Identifier { get; set; }

        [JsonPropertyName("videoURL")]
        public string? VideoUrl { get; set; }

        [JsonPropertyName("timedCaptions")]
        public Dictionary<string, string>? TimedCaptions { get; set; }

        // Helper to get collection name if we flatten it later or just pass it around
        [JsonIgnore]
        public string? CollectionName { get; set; }
    }
}
