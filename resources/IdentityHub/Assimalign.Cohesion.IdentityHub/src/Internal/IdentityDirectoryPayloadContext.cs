using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.IdentityHub.Internal;

/// <summary>
/// The source-generated serialization metadata of the directory's stored layouts, so a stored
/// user or group is read and written without reflection.
/// </summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(IdentityUserPayload))]
[JsonSerializable(typeof(IdentityGroupPayload))]
internal sealed partial class IdentityDirectoryPayloadContext : JsonSerializerContext
{
}
