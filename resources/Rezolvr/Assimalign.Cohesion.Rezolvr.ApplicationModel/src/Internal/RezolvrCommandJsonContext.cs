using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.Rezolvr.ApplicationModel;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AddARecordCommandPayload))]
[JsonSerializable(typeof(AddCnameRecordCommandPayload))]
internal sealed partial class RezolvrCommandJsonContext : JsonSerializerContext;

internal sealed record AddARecordCommandPayload(string Name, string Address, int TtlSeconds);
internal sealed record AddCnameRecordCommandPayload(string Name, string Target, int TtlSeconds);
