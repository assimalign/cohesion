using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Tests;

[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string))]
internal sealed partial class CommandPayloadJsonContext : JsonSerializerContext;
