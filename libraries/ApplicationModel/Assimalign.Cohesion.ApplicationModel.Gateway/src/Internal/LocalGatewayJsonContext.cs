using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(PortAllocationDocument))]
[JsonSerializable(typeof(LocalProcessRegistration))]
internal partial class LocalGatewayJsonContext : JsonSerializerContext;
