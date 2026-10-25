using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Web.Cors;

/// <summary>
/// Supplies reflection-free JSON serialization metadata for declarative CORS configuration.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(CorsConfiguration))]
[global::System.Diagnostics.DebuggerDisplay("CorsJsonContext")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed partial class CorsJsonContext : JsonSerializerContext
{
    private sealed class DebugView
    {
        private readonly CorsJsonContext _target;
        public DebugView(CorsJsonContext value) => _target = value;
        public global::System.Type Type => _target.GetType();
    }
}
