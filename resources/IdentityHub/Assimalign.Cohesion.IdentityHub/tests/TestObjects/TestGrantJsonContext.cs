using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.IdentityHub.Tests;

/// <summary>
/// The source-generated serializer a codec uses, so that no reflection reads a hub value.
/// </summary>
[JsonSerializable(typeof(TestGrant))]
internal sealed partial class TestGrantJsonContext : JsonSerializerContext
{
}
