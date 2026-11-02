using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Protocol.Tests;

[JsonSerializable(typeof(LogLevelSetting))]
internal sealed partial class TestJsonContext : JsonSerializerContext
{
}
