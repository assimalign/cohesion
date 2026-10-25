using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>Provides source-generated JSON metadata for runtime state contracts.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ResourceRuntimeManifest))]
[JsonSerializable(typeof(ResourceStateApplyContext))]
[JsonSerializable(typeof(ResourceStateValue))]
[JsonSerializable(typeof(ResourceStateObservation))]
[JsonSerializable(typeof(ResourceStateValidation))]
[JsonSerializable(typeof(ResourceStateApplyResult))]
[JsonSerializable(typeof(ResourceStateAdmission))]
[global::System.Diagnostics.DebuggerDisplay("ResourceStateJsonContext")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed partial class ResourceStateJsonContext : JsonSerializerContext
{

    private sealed class DebugView
    {
        private readonly ResourceStateJsonContext _target;
        public DebugView(ResourceStateJsonContext value) => _target = value;
        public global::System.Type Type => _target.GetType();
    }
}
