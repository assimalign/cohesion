using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

[global::System.Diagnostics.DebuggerDisplay("RezolvrCommandJsonContext")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AddARecordCommandPayload))]
[JsonSerializable(typeof(AddCnameRecordCommandPayload))]
internal sealed partial class RezolvrCommandJsonContext : JsonSerializerContext
{
    private sealed class DebugView
    {
        private readonly RezolvrCommandJsonContext _target;

        public DebugView(RezolvrCommandJsonContext value)
        {
            _target = value;
        }

        public global::System.Text.Json.JsonSerializerOptions? GeneratedSerializerOptions => _target.GeneratedSerializerOptions;
        public global::System.Text.Json.JsonSerializerOptions Options => _target.Options;
    }
}

internal sealed record AddARecordCommandPayload(string Name, string Address, int TtlSeconds);
internal sealed record AddCnameRecordCommandPayload(string Name, string Target, int TtlSeconds);
