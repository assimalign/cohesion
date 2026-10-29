using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Internal;

// This library's own content: the manifest an instance publishes, the publication its gateway
// answers with, and the result a transport returns. Property names are camel case and enumerations
// are written as their names. JsonResourceContentSerializer consults it before any other context.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ResourceManifest))]
[JsonSerializable(typeof(IReadOnlyList<ResourceStateSnapshot>))]
[JsonSerializable(typeof(ResourcePublication))]
[JsonSerializable(typeof(ResourceActionResult))]
internal sealed partial class ResourceJsonContext : JsonSerializerContext
{
}
