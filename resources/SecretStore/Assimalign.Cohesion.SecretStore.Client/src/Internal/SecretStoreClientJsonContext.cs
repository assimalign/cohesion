using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.SecretStore.Client;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ResourceCommand))]
internal sealed partial class SecretStoreClientJsonContext : JsonSerializerContext;
