using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Web.Hosting.Resources.Internal;

// The content of a Web resource's actions: the CORS policy the cors action reads, writes, and
// snapshots. The resource's default content serializer is JSON over this context.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WebCorsPolicy))]
internal sealed partial class WebResourceJsonContext : JsonSerializerContext
{
}
