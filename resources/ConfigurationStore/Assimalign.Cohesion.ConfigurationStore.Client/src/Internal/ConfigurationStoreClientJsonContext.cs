using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ConfigurationStore.Client;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<string>), TypeInfoPropertyName = "NamespaceNames")]
[JsonSerializable(typeof(Dictionary<string, string?>), TypeInfoPropertyName = "NamespaceValues")]
[JsonSerializable(typeof(ResourceCommand))]
internal sealed partial class ConfigurationStoreClientJsonContext : JsonSerializerContext;
