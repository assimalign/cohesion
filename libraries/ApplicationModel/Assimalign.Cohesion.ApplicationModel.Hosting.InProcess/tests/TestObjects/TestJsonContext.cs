using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.InProcess.Tests;

[JsonSerializable(typeof(ProgramEnvironment))]
internal sealed partial class TestJsonContext : JsonSerializerContext
{
}
