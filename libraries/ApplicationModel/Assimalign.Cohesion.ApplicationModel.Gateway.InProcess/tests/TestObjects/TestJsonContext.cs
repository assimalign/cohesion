using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Tests;

[JsonSerializable(typeof(ProgramEnvironment))]
internal sealed partial class TestJsonContext : JsonSerializerContext
{
}
