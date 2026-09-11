using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ConfigurationStore.ApplicationModel;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SetConfigurationValueCommandPayload))]
[JsonSerializable(typeof(RemoveConfigurationValueCommandPayload))]
[JsonSerializable(typeof(string))]
internal sealed partial class ConfigurationStoreCommandJsonContext : JsonSerializerContext;

internal sealed record SetConfigurationValueCommandPayload(string Namespace, string Key, JsonElement Value);
internal sealed record RemoveConfigurationValueCommandPayload(string Namespace, string Key);
