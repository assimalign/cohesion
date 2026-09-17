using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.SecretStore.Hosting;

internal sealed class SecretStoreResourceCommandHandler(string kind, SecretStoreRepository repository,
    CertificateAuthorityManager authority) : IResourceCommandHandler
{
    internal const string AddSecret = "secretstore.add-secret";
    internal const string IssueCertificate = "secretstore.issue-certificate";
    public string Kind { get; } = kind;

    public async ValueTask<ReadOnlyMemory<byte>> ExecuteAsync(ResourceCommand command, CancellationToken cancellationToken = default)
    {
        using JsonDocument document = JsonDocument.Parse(command.Payload);
        JsonElement payload = document.RootElement;
        string key = Required(payload, Kind == AddSecret ? "path" : "name");
        if (key != command.Key)
        {
            throw new ResourceCommandRejectedException($"{Kind} payload key '{key}' must match command key '{command.Key}'.");
        }
        if (Kind == AddSecret)
        {
            string source = Required(payload, "source");
            if (source.StartsWith("literal:", StringComparison.OrdinalIgnoreCase))
            {
                throw new ResourceCommandRejectedException("secretstore.add-secret forbids literal sources; declare a parameter or resource source.");
            }
            if (!payload.TryGetProperty("resolvedValue", out JsonElement value) || value.ValueKind != JsonValueKind.String)
            {
                throw new ResourceCommandRejectedException($"secretstore.add-secret source '{source}' is unresolved for path '{key}'; the delivering gateway must resolve the source before dispatch.");
            }
            byte[] bytes = value.GetBytesFromBase64();
            try
            {
                await repository.StoreCommandSecretAsync(command, source, bytes, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        else
        {
            string subject = Required(payload, "subject");
            if (key.Contains('/'))
            {
                throw new ResourceCommandRejectedException($"{Kind} certificate name '{key}' must be one segment.");
            }
            bool hasAlternatives = payload.TryGetProperty("subjectAlternativeNames", out JsonElement names);
            if (hasAlternatives && names.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
            {
                throw new JsonException("Certificate subjectAlternativeNames must be a string array.");
            }
            string[] alternatives = hasAlternatives && names.ValueKind == JsonValueKind.Array
                ? names.EnumerateArray().Select(static name =>
                    name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString())
                        ? name.GetString()! : throw new JsonException("Certificate alternative names must be nonblank strings.")).ToArray()
                : [];
            try
            {
                await authority.GetCertificatePemAsync("certs/" + key, subject, alternatives, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is CryptographicException or ArgumentException or NotSupportedException)
            {
                throw new ResourceCommandRejectedException($"{Kind} cannot honor subject/SANs for certificate '{key}': {exception.Message}");
            }
        }
        return ReadOnlyMemory<byte>.Empty;
    }

    public async ValueTask<ReadOnlyMemory<byte>> DeleteAsync(ResourceCommand command, CancellationToken cancellationToken = default)
    {
        if (Kind == AddSecret)
        {
            await repository.DeleteCommandSecretAsync(command, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await authority.DeleteCertificateAsync(command.Key, cancellationToken).ConfigureAwait(false);
        }
        return ReadOnlyMemory<byte>.Empty;
    }

    private static string Required(JsonElement payload, string property)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new JsonException($"A secret-store command requires a nonblank '{property}'.");
        }
        return value.GetString()!;
    }
}
