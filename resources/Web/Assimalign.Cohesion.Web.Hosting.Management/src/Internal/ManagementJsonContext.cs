using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Web.Hosting.Management.Internal;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CertificateStateValue))]
[global::System.Diagnostics.DebuggerDisplay("ManagementJsonContext")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed partial class ManagementJsonContext : JsonSerializerContext
{

    private sealed class DebugView
    {
        private readonly ManagementJsonContext _target;
        public DebugView(ManagementJsonContext value) => _target = value;
        public global::System.Type Type => _target.GetType();
    }
}
