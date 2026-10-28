using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Web.Hosting.Resources.Internal;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WebCorsPolicy))]
internal sealed partial class WebResourceJsonContext : JsonSerializerContext
{
}
