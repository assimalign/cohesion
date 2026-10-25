using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

internal sealed record EchoInput(string Text);

internal sealed record EchoResult(string Text, int Length);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EchoInput))]
[JsonSerializable(typeof(EchoResult))]
internal sealed partial class EchoActionContext : JsonSerializerContext
{
}
