using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GatewayDeploymentManifest))]
internal sealed partial class GatewayJsonContext : JsonSerializerContext
{
}
