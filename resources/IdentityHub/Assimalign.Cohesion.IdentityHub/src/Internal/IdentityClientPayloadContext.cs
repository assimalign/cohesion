using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.IdentityHub.Internal;

/// <summary>
/// The source-generated serialization metadata of <see cref="IdentityClientPayload"/>, so a
/// stored registration is read and written without reflection.
/// </summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(IdentityClientPayload))]
internal sealed partial class IdentityClientPayloadContext : JsonSerializerContext
{
}
