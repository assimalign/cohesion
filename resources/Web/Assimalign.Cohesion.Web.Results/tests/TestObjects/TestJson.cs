using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Web.Results.Tests.TestObjects;

/// <summary>
/// A representative open DTO for the JSON result tests.
/// </summary>
internal sealed record TestPayload(string Name, int Count);

/// <summary>
/// The source-generated serialization context the JSON result tests supply as
/// <c>JsonTypeInfo&lt;T&gt;</c> — the same zero-reflection shape a real endpoint author provides.
/// </summary>
[JsonSerializable(typeof(TestPayload))]
internal partial class TestJsonContext : JsonSerializerContext
{
}
