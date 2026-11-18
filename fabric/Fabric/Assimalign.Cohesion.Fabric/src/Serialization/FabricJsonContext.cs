using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// The source-generated JSON contract of the fabric root's document types.
/// </summary>
/// <remarks>
/// Unknown members are fatal (<see cref="JsonUnmappedMemberHandling.Disallow"/>) at every tier
/// of the document, so a misspelled field or a field from a newer version fails validation
/// instead of being dropped. Names are camelCase and enums are strings, as the ResourceManager's
/// API serves them. A fabric area ships its own context for its spec types, with the same options.
/// </remarks>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ResourceEnvelope))]
[JsonSerializable(typeof(FabricEnvironmentPolicy))]
[JsonSerializable(typeof(FabricFault))]
[JsonSerializable(typeof(FabricDiagnostics))]
[JsonSerializable(typeof(ResourceProviderManifest))]
public sealed partial class FabricJsonContext : JsonSerializerContext;
