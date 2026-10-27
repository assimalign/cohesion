using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

[JsonSerializable(typeof(LogLevelSetting))]
internal sealed partial class TestJsonContext : JsonSerializerContext
{
}
