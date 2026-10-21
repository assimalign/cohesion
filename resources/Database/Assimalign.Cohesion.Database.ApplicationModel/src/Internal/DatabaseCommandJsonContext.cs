using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AddDatabaseCommandPayload))]
[JsonSerializable(typeof(AddPrincipalCommandPayload))]
internal sealed partial class DatabaseCommandJsonContext : JsonSerializerContext;
