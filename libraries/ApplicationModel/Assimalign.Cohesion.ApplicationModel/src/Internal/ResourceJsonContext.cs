using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

// This library's own content: the manifest an instance publishes, the publication its gateway
// answers with, and the result an action returns. Property names are camel case and enumerations
// are written as their names. JsonApplicationContentSerializer consults it before any other context.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ApplicationInstanceManifest))]
[JsonSerializable(typeof(IReadOnlyList<ApplicationActionSnapshot>))]
[JsonSerializable(typeof(ApplicationInstancePublication))]
[JsonSerializable(typeof(ApplicationActionResult))]
internal sealed partial class ResourceJsonContext : JsonSerializerContext
{
}
