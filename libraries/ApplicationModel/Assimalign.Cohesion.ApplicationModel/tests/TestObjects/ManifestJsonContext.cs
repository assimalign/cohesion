using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

[JsonSerializable(typeof(ApplicationManifest))]
internal sealed partial class ManifestJsonContext : JsonSerializerContext
{
}
