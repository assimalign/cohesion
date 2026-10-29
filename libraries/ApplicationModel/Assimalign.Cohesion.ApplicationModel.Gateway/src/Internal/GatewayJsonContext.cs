using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ApplicationDeploymentManifest))]
[JsonSerializable(typeof(ApplicationResourceManifest))]
internal sealed partial class GatewayJsonContext : JsonSerializerContext
{
}
