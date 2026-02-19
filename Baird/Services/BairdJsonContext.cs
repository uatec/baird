using System.Collections.Generic;
using System.Text.Json.Serialization;
using Baird.Models;
using Baird.Services;

namespace Baird
{
    [JsonSerializable(typeof(List<HistoryItem>))]
    [JsonSerializable(typeof(List<SearchTermItem>))]
    [JsonSerializable(typeof(List<MediaItem>))]
    [JsonSerializable(typeof(List<MediaItemData>))]
    [JsonSerializable(typeof(MediaItemData))]
    [JsonSerializable(typeof(List<string>))]
    [JsonSerializable(typeof(Dictionary<string, object>))]
    [JsonSerializable(typeof(JellyseerrSearchResult))]
    [JsonSerializable(typeof(List<JellyseerrSearchResult>))]
    [JsonSerializable(typeof(JellyseerrRequest))]
    [JsonSerializable(typeof(List<JellyseerrRequest>))]
    [JsonSerializable(typeof(JellyseerrRequestResponse))]
    [JsonSerializable(typeof(ScreensaverResponse))]
    internal partial class BairdJsonContext : JsonSerializerContext
    {
    }
}
