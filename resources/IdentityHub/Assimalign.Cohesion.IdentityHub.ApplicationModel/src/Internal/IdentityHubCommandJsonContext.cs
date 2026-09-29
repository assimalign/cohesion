using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AddAudienceCommandPayload))]
[JsonSerializable(typeof(AddClientCommandPayload))]
internal sealed partial class IdentityHubCommandJsonContext : JsonSerializerContext;

internal sealed record AddAudienceCommandPayload(string Name);
internal sealed record AddClientCommandPayload(string ClientId, string[] Audiences, string CredentialSource);
