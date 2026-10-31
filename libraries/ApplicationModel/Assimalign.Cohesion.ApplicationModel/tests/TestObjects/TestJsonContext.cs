using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

[JsonSerializable(typeof(LogLevelSetting))]
internal sealed partial class TestJsonContext : JsonSerializerContext
{
}
