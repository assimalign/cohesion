using System.Text.Json;
using System.Text.Json.Serialization;

using Assimalign.Cohesion.Fabric;

namespace Assimalign.Cohesion.Workloads;

/// <summary>
/// The source-generated JSON contract of the Workloads area's spec, with the fabric root's
/// options: camelCase names, string enums, and unknown members refused.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(FabricStringValueJsonConverter)])]
[JsonSerializable(typeof(WorkloadSpec))]
[JsonSerializable(typeof(WorkloadCapabilities))]
public sealed partial class WorkloadsJsonContext : JsonSerializerContext;
