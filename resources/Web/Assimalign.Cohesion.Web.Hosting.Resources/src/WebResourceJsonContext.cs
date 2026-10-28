using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Web.Hosting.Resources;

/// <summary>
/// Source-generated JSON metadata for the content of a Web resource's actions: the
/// <see cref="WebCorsPolicy"/> the <c>cors</c> action reads, writes, and snapshots.
/// </summary>
/// <remarks>
/// Property names are camel case. A Web resource's default content serializer is
/// <c>JsonResourceContentSerializer.Create(WebResourceJsonContext.Default)</c>; a gateway platform
/// that exchanges JSON with Web resources creates its serializer with this context too.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WebCorsPolicy))]
public sealed partial class WebResourceJsonContext : JsonSerializerContext
{
}
