using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration.Tests;

/// <summary>
/// Source-generated metadata for the declared command payloads the tests build.
/// </summary>
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class CommandPayloadJsonContext : JsonSerializerContext;
