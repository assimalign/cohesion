using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AddDatabaseCommandPayload))]
[JsonSerializable(typeof(AddPrincipalCommandPayload))]
internal sealed partial class DatabaseCommandJsonContext : JsonSerializerContext;

internal sealed record AddDatabaseCommandPayload(string Database, string? Engine);
internal sealed record AddPrincipalCommandPayload(string Database, string Name);
