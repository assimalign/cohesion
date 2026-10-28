using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Source-generated JSON metadata for this library's own content: the
/// <see cref="ResourceManifest"/> an instance publishes, the <see cref="ResourcePublication"/> its
/// gateway answers with, and the <see cref="ResourceActionResult"/> a transport returns.
/// </summary>
/// <remarks>
/// Property names are camel case and enumerations are written as their names, so a manifest reads
/// <c>{ "provider": "Web", "name": "orders-api", "key": "…", "actions": [ { "name": "cors", "kind": "ReadWrite", "isStateful": true } ] }</c>.
/// <see cref="JsonResourceContentSerializer"/> consults it before any other context.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ResourceManifest))]
[JsonSerializable(typeof(IReadOnlyList<ResourceStateSnapshot>))]
[JsonSerializable(typeof(ResourcePublication))]
[JsonSerializable(typeof(ResourceActionResult))]
public sealed partial class ResourceJsonContext : JsonSerializerContext
{
}
