using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

// The record file's JSON. The snapshot converter replaces ApplicationActionSnapshot's own converter, which
// embeds the value as JSON, so values in any content serializer's format survive the file.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true,
    Converters = new[] { typeof(ApplicationGatewayStoreSnapshotJsonConverter) })]
[JsonSerializable(typeof(ApplicationGatewayRecord))]
internal sealed partial class ApplicationGatewayStoreJsonContext : JsonSerializerContext
{
}
