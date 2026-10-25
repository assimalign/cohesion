using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ResourceManagementState))]
[JsonSerializable(typeof(ResourceDesiredValue))]
[JsonSerializable(typeof(ResourceStateChange))]
[JsonSerializable(typeof(ResourceStateOperation))]
[JsonSerializable(typeof(ResourceInstanceInventory))]
[global::System.Diagnostics.DebuggerDisplay("ResourceManagementJsonContext")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed partial class ResourceManagementJsonContext : JsonSerializerContext
{

    private sealed class DebugView
    {
        private readonly ResourceManagementJsonContext _target;
        public DebugView(ResourceManagementJsonContext value) => _target = value;
        public global::System.Type Type => _target.GetType();
    }
}
