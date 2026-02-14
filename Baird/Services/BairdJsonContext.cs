using System.Text.Json.Serialization;
using Baird.Models;
using Baird.Services;

namespace Baird;

[JsonSerializable(typeof(List<HistoryItem>))]
[JsonSerializable(typeof(List<SearchTermItem>))]
internal partial class BairdJsonContext : JsonSerializerContext
{
}
