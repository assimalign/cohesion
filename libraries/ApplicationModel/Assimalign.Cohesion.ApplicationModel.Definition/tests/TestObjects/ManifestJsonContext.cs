using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Definition.Tests;

[JsonSerializable(typeof(ApplicationManifest))]
internal sealed partial class ManifestJsonContext : JsonSerializerContext
{
}
