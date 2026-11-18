using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Fabric.InProcess.Internal;

/// <summary>
/// The source-generated JSON contract of the in-process platform's extension blocks, with the
/// fabric root's options: camelCase names and unknown members refused.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(InProcessWorkloadSettings))]
internal sealed partial class InProcessJsonContext : JsonSerializerContext;
