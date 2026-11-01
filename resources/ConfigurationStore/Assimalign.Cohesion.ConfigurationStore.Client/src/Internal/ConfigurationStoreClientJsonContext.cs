using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ConfigurationStore.Client.Internal;

[global::System.Diagnostics.DebuggerDisplay("ConfigurationStoreClientJsonContext")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<string>), TypeInfoPropertyName = "NamespaceNames")]
[JsonSerializable(typeof(Dictionary<string, string?>), TypeInfoPropertyName = "NamespaceValues")]
internal sealed partial class ConfigurationStoreClientJsonContext : JsonSerializerContext
{
    private sealed class DebugView
    {
        private readonly ConfigurationStoreClientJsonContext _target;

        public DebugView(ConfigurationStoreClientJsonContext value)
        {
            _target = value;
        }

        public global::System.Text.Json.JsonSerializerOptions? GeneratedSerializerOptions => _target.GeneratedSerializerOptions;
        public global::System.Text.Json.JsonSerializerOptions Options => _target.Options;
    }
}
