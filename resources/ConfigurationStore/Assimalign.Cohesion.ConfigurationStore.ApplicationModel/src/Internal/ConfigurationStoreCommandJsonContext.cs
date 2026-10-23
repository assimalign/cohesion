using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

[global::System.Diagnostics.DebuggerDisplay("ConfigurationStoreCommandJsonContext")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AddConfigurationNamespaceCommandPayload))]
[JsonSerializable(typeof(SetConfigurationValueCommandPayload))]
[JsonSerializable(typeof(RemoveConfigurationValueCommandPayload))]
[JsonSerializable(typeof(string))]
internal sealed partial class ConfigurationStoreCommandJsonContext : JsonSerializerContext
{
    private sealed class DebugView
    {
        private readonly ConfigurationStoreCommandJsonContext _target;

        public DebugView(ConfigurationStoreCommandJsonContext value)
        {
            _target = value;
        }

        public global::System.Text.Json.JsonSerializerOptions? GeneratedSerializerOptions => _target.GeneratedSerializerOptions;
        public global::System.Text.Json.JsonSerializerOptions Options => _target.Options;
    }
}

internal sealed record SetConfigurationValueCommandPayload(string Namespace, string Key, JsonElement Value);
internal sealed record RemoveConfigurationValueCommandPayload(string Namespace, string Key);
internal sealed record AddConfigurationNamespaceCommandPayload(string Name, SortedDictionary<string, string?> Seed);
