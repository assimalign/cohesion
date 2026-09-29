using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

/// <summary>
/// Parses the <c>trusted-issuers.json</c> document a SecretStore exports:
/// <c>{"issuers":[{"issuer":"...","trustKey":{...},"allowedCommandKinds":["..."]}]}</c>.
/// </summary>
/// <remarks>
/// The parse rules and failure messages are the gateway's own reader of this document, moved here
/// with the rest of the SecretStore wire knowledge. Members the gateway does not read (the store
/// also exports each grant's <c>owner</c>) are ignored.
/// </remarks>
internal static class SecretStoreTrustedIssuerDocument
{
    /// <summary>
    /// Parses one exported trusted-issuers document.
    /// </summary>
    /// <param name="content">The UTF-8 JSON document.</param>
    /// <returns>The issuers in document order.</returns>
    /// <exception cref="JsonException"><paramref name="content"/> is not JSON.</exception>
    /// <exception cref="InvalidDataException">
    /// The document has no <c>issuers</c> array, an entry lacks a string <c>issuer</c> or a
    /// <c>trustKey</c>, an issuer occurs twice, an <c>allowedCommandKinds</c> member is not an
    /// array of nonblank strings, or a trust key is not a valid ES256 public JWK.
    /// </exception>
    internal static IReadOnlyList<TrustedIssuer> Parse(ReadOnlyMemory<byte> content)
    {
        using JsonDocument document = JsonDocument.Parse(content);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("issuers", out JsonElement issuers) ||
            issuers.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "The trusted-issuers document must be an object containing an 'issuers' array.");
        }

        var result = new List<TrustedIssuer>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in issuers.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("issuer", out JsonElement issuerProperty) ||
                issuerProperty.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("trustKey", out JsonElement keyProperty))
            {
                throw new InvalidDataException(
                    "Every trusted issuer must contain string 'issuer' and object 'trustKey' properties.");
            }

            string issuer = issuerProperty.GetString()!;
            if (!names.Add(issuer))
            {
                throw new InvalidDataException(
                    $"Trusted issuer '{issuer}' occurs more than once in the document.");
            }

            try
            {
                var allowedKinds = new List<string>();
                if (item.TryGetProperty("allowedCommandKinds", out JsonElement kinds))
                {
                    if (kinds.ValueKind != JsonValueKind.Array)
                    {
                        throw new InvalidDataException(
                            $"Trusted issuer '{issuer}' allowedCommandKinds must be a string array.");
                    }

                    foreach (JsonElement kind in kinds.EnumerateArray())
                    {
                        if (kind.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(kind.GetString()))
                        {
                            throw new InvalidDataException(
                                $"Trusted issuer '{issuer}' allowedCommandKinds requires nonblank strings.");
                        }

                        allowedKinds.Add(kind.GetString()!);
                    }
                }

                result.Add(new TrustedIssuer(issuer, keyProperty, allowedKinds));
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    $"Trusted issuer '{issuer}' has an invalid public key: {exception.Message}",
                    exception);
            }
        }

        return result;
    }
}
