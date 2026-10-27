using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

[global::System.Diagnostics.DebuggerDisplay("DatabaseCommandJsonContext")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.ApplicationModel.Internal.DatabaseCommandJsonContext.DebugView))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AddDatabaseCommandPayload))]
[JsonSerializable(typeof(AddPrincipalCommandPayload))]
internal sealed partial class DatabaseCommandJsonContext : JsonSerializerContext
{
    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.ApplicationModel.Internal.DatabaseCommandJsonContext value)
        {
            _ = value;
        }
    }
}
