using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting.Internal;

internal sealed class ConfigurationTrustedIssuer
{
    internal ConfigurationTrustedIssuer(string issuer, JsonElement publicKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        if (publicKey.ValueKind is not JsonValueKind.Object)
        {
            throw new ArgumentException("A trusted issuer key must be a JSON object.", nameof(publicKey));
        }

        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in publicKey.EnumerateObject())
        {
            if (!members.Add(property.Name) ||
                property.Name is not ("kty" or "crv" or "x" or "y" or "kid" or "alg" or "use"))
            {
                throw new ArgumentException(
                    $"A trusted issuer key contains unsupported or duplicate member '{property.Name}'.",
                    nameof(publicKey));
            }
        }

        string keyType = RequiredString(publicKey, "kty");
        string curve = RequiredString(publicKey, "crv");
        string encodedX = RequiredString(publicKey, "x");
        string encodedY = RequiredString(publicKey, "y");
        string keyId = RequiredString(publicKey, "kid");
        string algorithm = RequiredString(publicKey, "alg");
        string use = RequiredString(publicKey, "use");
        if (!string.Equals(keyType, "EC", StringComparison.Ordinal) ||
            !string.Equals(curve, "P-256", StringComparison.Ordinal) ||
            !string.Equals(algorithm, "ES256", StringComparison.Ordinal) ||
            !string.Equals(use, "sig", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A trusted issuer key must be an EC P-256 key for ES256 signatures.",
                nameof(publicKey));
        }

        byte[] x;
        byte[] y;
        try
        {
            x = Base64Url.DecodeFromChars(encodedX);
            y = Base64Url.DecodeFromChars(encodedY);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "A trusted issuer key has invalid base64url coordinates.",
                nameof(publicKey),
                exception);
        }

        try
        {
            if (x.Length is not 32 || y.Length is not 32)
            {
                throw new ArgumentException(
                    "A trusted issuer P-256 key must have 32-byte coordinates.",
                    nameof(publicKey));
            }

            try
            {
                using ECDsa verifier = ECDsa.Create();
                verifier.ImportParameters(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = x, Y = y },
                });
            }
            catch (Exception exception) when (
                exception is ArgumentException or CryptographicException or PlatformNotSupportedException)
            {
                throw new ArgumentException(
                    "A trusted issuer key is not a valid P-256 point.",
                    nameof(publicKey),
                    exception);
            }

            if (!string.Equals(keyId, ComputeThumbprint(encodedX, encodedY), StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "A trusted issuer key identifier must equal its RFC 7638 thumbprint.",
                    nameof(publicKey));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(x);
            CryptographicOperations.ZeroMemory(y);
        }

        Issuer = issuer;
        PublicKey = publicKey.Clone();
        KeyId = keyId;
    }

    internal string Issuer { get; }

    internal string KeyId { get; }

    internal JsonElement PublicKey { get; }

    private static string RequiredString(JsonElement key, string name)
    {
        if (!key.TryGetProperty(name, out JsonElement property) ||
            property.ValueKind is not JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new ArgumentException(
                $"A trusted issuer key requires string member '{name}'.",
                nameof(key));
        }

        return property.GetString()!;
    }

    private static string ComputeThumbprint(string encodedX, string encodedY)
    {
        var canonical = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(canonical))
        {
            writer.WriteStartObject();
            writer.WriteString("crv", "P-256");
            writer.WriteString("kty", "EC");
            writer.WriteString("x", encodedX);
            writer.WriteString("y", encodedY);
            writer.WriteEndObject();
        }

        byte[] hash = SHA256.HashData(canonical.WrittenSpan);
        try
        {
            return Base64Url.EncodeToString(hash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }
}
