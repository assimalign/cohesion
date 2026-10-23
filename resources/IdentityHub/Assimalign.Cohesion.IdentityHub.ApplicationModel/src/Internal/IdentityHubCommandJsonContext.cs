using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

[global::System.Diagnostics.DebuggerDisplay("IdentityHubCommandJsonContext")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AddAudienceCommandPayload))]
[JsonSerializable(typeof(AddClientCommandPayload))]
internal sealed partial class IdentityHubCommandJsonContext : JsonSerializerContext
{
    private sealed class DebugView
    {
        private readonly IdentityHubCommandJsonContext _target;

        public DebugView(IdentityHubCommandJsonContext value)
        {
            _target = value;
        }

        public global::System.Text.Json.JsonSerializerOptions? GeneratedSerializerOptions => _target.GeneratedSerializerOptions;
        public global::System.Text.Json.JsonSerializerOptions Options => _target.Options;
    }
}

internal sealed record AddAudienceCommandPayload(string Name);
internal sealed record AddClientCommandPayload(string ClientId, string[] Audiences, string CredentialSource);
