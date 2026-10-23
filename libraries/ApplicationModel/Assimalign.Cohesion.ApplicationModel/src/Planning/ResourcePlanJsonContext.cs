using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Provides reflection-free System.Text.Json metadata for the realization-plan wire contract.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("ResourcePlanJsonContext")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Default,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ResourcePlan))]
public sealed partial class ResourcePlanJsonContext : JsonSerializerContext
{
    private sealed class DebugView
    {
        private readonly ResourcePlanJsonContext _target;

        public DebugView(ResourcePlanJsonContext value)
        {
            _target = value;
        }

        public global::System.Text.Json.JsonSerializerOptions? GeneratedSerializerOptions => _target.GeneratedSerializerOptions;
        public global::System.Text.Json.JsonSerializerOptions Options => _target.Options;
    }
}
