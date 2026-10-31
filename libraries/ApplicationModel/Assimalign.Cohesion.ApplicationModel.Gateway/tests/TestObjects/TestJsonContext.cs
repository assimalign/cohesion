using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

[JsonSerializable(typeof(LogLevelSetting))]
[JsonSerializable(typeof(ScaleSetting))]
internal sealed partial class TestJsonContext : JsonSerializerContext
{
}
