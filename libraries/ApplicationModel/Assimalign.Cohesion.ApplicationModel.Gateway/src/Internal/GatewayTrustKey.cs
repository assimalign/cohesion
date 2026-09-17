using System;
using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Owns an application's gateway signing key and its public JWK representation.
/// </summary>
internal sealed class GatewayTrustKey : IDisposable
{
    private const string P256CurveOid = "1.2.840.10045.3.1.7";

    private ECDsa? _privateKey;

    internal GatewayTrustKey(ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);

        PublicJwk = CreatePublicJwk(privateKey, out string keyId);
        KeyId = keyId;
        _privateKey = privateKey;
    }

    /// <summary>Gets the owned ECDSA P-256 private key.</summary>
    public ECDsa PrivateKey => Volatile.Read(ref _privateKey)
        ?? throw new ObjectDisposedException(nameof(GatewayTrustKey));

    /// <summary>Gets the RFC 7638 thumbprint used as the JOSE key identifier.</summary>
    public string KeyId { get; }

    /// <summary>Gets the public-only EC JWK for the signing key.</summary>
    public JsonElement PublicJwk { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _privateKey, null)?.Dispose();
    }

    private static JsonElement CreatePublicJwk(ECDsa key, out string keyId)
    {
        ECParameters parameters = key.ExportParameters(includePrivateParameters: false);
        if (!string.Equals(parameters.Curve.Oid.Value, P256CurveOid, StringComparison.Ordinal) ||
            parameters.Q.X is not { Length: 32 } x ||
            parameters.Q.Y is not { Length: 32 } y)
        {
            throw new ArgumentException(
                "A gateway trust key must use the NIST P-256 curve.",
                nameof(key));
        }

        string encodedX = Base64Url.EncodeToString(x);
        string encodedY = Base64Url.EncodeToString(y);

        var canonicalJwk = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(canonicalJwk))
        {
            // RFC 7638 requires lexicographic member ordering for this EC JWK subset.
            writer.WriteStartObject();
            writer.WriteString("crv", "P-256");
            writer.WriteString("kty", "EC");
            writer.WriteString("x", encodedX);
            writer.WriteString("y", encodedY);
            writer.WriteEndObject();
            writer.Flush();
        }

        byte[] thumbprint = SHA256.HashData(canonicalJwk.WrittenSpan);
        try
        {
            keyId = Base64Url.EncodeToString(thumbprint);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(thumbprint);
        }

        var publicJwk = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(publicJwk))
        {
            writer.WriteStartObject();
            writer.WriteString("kty", "EC");
            writer.WriteString("crv", "P-256");
            writer.WriteString("x", encodedX);
            writer.WriteString("y", encodedY);
            writer.WriteString("kid", keyId);
            writer.WriteString("alg", "ES256");
            writer.WriteString("use", "sig");
            writer.WriteEndObject();
            writer.Flush();
        }

        using JsonDocument document = JsonDocument.Parse(publicJwk.WrittenMemory);
        return document.RootElement.Clone();
    }
}
