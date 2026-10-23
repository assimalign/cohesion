using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Provides NativeAOT-safe JSON metadata for the Cohesion resource-manifest contract.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("ResourceManifestJsonContext")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ResourceManifest))]
public sealed partial class ResourceManifestJsonContext : JsonSerializerContext
{
    private sealed class DebugView
    {
        private readonly ResourceManifestJsonContext _target;

        public DebugView(ResourceManifestJsonContext value)
        {
            _target = value;
        }

        public global::System.Text.Json.JsonSerializerOptions? GeneratedSerializerOptions => _target.GeneratedSerializerOptions;
        public global::System.Text.Json.JsonSerializerOptions Options => _target.Options;
    }
}
