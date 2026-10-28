using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Source-generated JSON metadata for what a resource instance and its gateway exchange: the
/// <see cref="ResourceManifest"/> the instance publishes and the <see cref="ResourceStateSnapshot"/>
/// list the gateway answers with.
/// </summary>
/// <remarks>
/// Property names are camel case and enumerations are written as their names, so a manifest reads
/// <c>{ "provider": "Web", "name": "orders-api", "key": "…", "actions": [ { "name": "cors", "kind": "ReadWrite", "isStateful": true } ] }</c>.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ResourceManifest))]
[JsonSerializable(typeof(IReadOnlyList<ResourceStateSnapshot>))]
public sealed partial class ResourceJsonContext : JsonSerializerContext
{
}
