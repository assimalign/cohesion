using System.Text.Json.Serialization;

namespace Assimalign.Cohesion.SecretStore.ApplicationModel;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AddSecretCommandPayload))]
[JsonSerializable(typeof(IssueCertificateCommandPayload))]
internal sealed partial class SecretStoreCommandJsonContext : JsonSerializerContext;

internal sealed record AddSecretCommandPayload(string Path, string Source);
internal sealed record IssueCertificateCommandPayload(string Name, string Subject, string[] SubjectAlternativeNames);
