using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ResourceStateApplyRequest))]
[global::System.Diagnostics.DebuggerDisplay("ResourceStateHttpJsonContext")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed partial class ResourceStateHttpJsonContext : JsonSerializerContext
{

    private sealed class DebugView
    {
        private readonly ResourceStateHttpJsonContext _target;
        public DebugView(ResourceStateHttpJsonContext value) => _target = value;
        public global::System.Type Type => _target.GetType();
    }
}
