using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

[global::System.Diagnostics.DebuggerDisplay("SecretStoreCommandJsonContext")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AddSecretCommandPayload))]
[JsonSerializable(typeof(IssueCertificateCommandPayload))]
internal sealed partial class SecretStoreCommandJsonContext : JsonSerializerContext
{
    private sealed class DebugView
    {
        private readonly SecretStoreCommandJsonContext _target;

        public DebugView(SecretStoreCommandJsonContext value)
        {
            _target = value;
        }

        public global::System.Text.Json.JsonSerializerOptions? GeneratedSerializerOptions => _target.GeneratedSerializerOptions;
        public global::System.Text.Json.JsonSerializerOptions Options => _target.Options;
    }
}

internal sealed record AddSecretCommandPayload(string Path, string Source);
internal sealed record IssueCertificateCommandPayload(string Name, string Subject, string[] SubjectAlternativeNames);
