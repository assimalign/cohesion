using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.SecretStore.Client.Internal;

[global::System.Diagnostics.DebuggerDisplay("SecretStoreClientJsonContext")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ResourceCommand))]
internal sealed partial class SecretStoreClientJsonContext : JsonSerializerContext
{
    private sealed class DebugView
    {
        private readonly SecretStoreClientJsonContext _target;

        public DebugView(SecretStoreClientJsonContext value)
        {
            _target = value;
        }

        public global::System.Text.Json.JsonSerializerOptions? GeneratedSerializerOptions => _target.GeneratedSerializerOptions;
        public global::System.Text.Json.JsonSerializerOptions Options => _target.Options;
    }
}
