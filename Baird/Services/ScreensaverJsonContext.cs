using System.Text.Json.Serialization;
using Baird.Models;

namespace Baird.Services;

[JsonSerializable(typeof(ScreensaverAsset))]
[JsonSerializable(typeof(ScreensaverCollection))]
[JsonSerializable(typeof(ScreensaverResponse))]
public partial class ScreensaverJsonContext : JsonSerializerContext
{
}
