using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>Provides AOT-safe management API metadata without exposing private coordination storage.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ResourceStateOperation))]
[JsonSerializable(typeof(ResourceDesiredValue))]
[JsonSerializable(typeof(ResourceInstanceInventory))]
[global::System.Diagnostics.DebuggerDisplay("ResourceManagementWireJsonContext")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed partial class ResourceManagementWireJsonContext : JsonSerializerContext
{

    private sealed class DebugView
    {
        private readonly ResourceManagementWireJsonContext _target;
        public DebugView(ResourceManagementWireJsonContext value) => _target = value;
        public global::System.Type Type => _target.GetType();
    }
}
