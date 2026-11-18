using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Networking;

/// <summary>
/// The source-generated JSON contract of the Networking area's spec, with the fabric root's
/// options: camelCase names, string enums, and unknown members refused.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ExposureSpec))]
public sealed partial class NetworkingJsonContext : JsonSerializerContext;
