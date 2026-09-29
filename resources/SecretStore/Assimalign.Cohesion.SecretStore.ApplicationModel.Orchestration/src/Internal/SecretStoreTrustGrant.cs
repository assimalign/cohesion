using System;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using SecretStoreResourceCommand = Assimalign.Cohesion.SecretStore.Client.ResourceCommand;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

/// <summary>
/// Builds the <c>cohesion.trust.add</c> command that persists one trusted issuer in a SecretStore.
/// </summary>
/// <remarks>
/// <para>
/// The payload is the issuer's public JWK exactly as it was parsed. When the grant restricts
/// command kinds, the payload is the envelope
/// <c>{"trustKey":&lt;jwk&gt;,"allowedCommandKinds":["..."]}</c> instead.
/// </para>
/// <para>
/// The command id is <c>trust-</c> followed by the lowercase hex SHA-256 of
/// <c>owner + "\n" + issuer + "\n"</c> (UTF-8) and the payload bytes, so an identical grant always
/// carries the same id. The command key is the issuer name.
/// </para>
/// <para>
/// This is byte-for-byte the command the gateway's built-in store client sent before the provider
/// seams existed; the store keys its idempotency on these values.
/// </para>
/// </remarks>
internal static class SecretStoreTrustGrant
{
    /// <summary>
    /// Creates the trust-grant command for one issuer.
    /// </summary>
    /// <param name="owner">The ownership identity recorded with the grant.</param>
    /// <param name="issuer">The issuer to trust.</param>
    /// <returns>The command to send to the store's control plane.</returns>
    /// <exception cref="ArgumentException"><paramref name="owner"/> is blank.</exception>
    internal static SecretStoreResourceCommand Create(string owner, TrustedIssuer issuer)
    {
        ReadOnlyMemory<byte> payload = CreatePayload(issuer);
        byte[] identity = Encoding.UTF8.GetBytes(owner + "\n" + issuer.Issuer + "\n");
        byte[] commandHash;
        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hasher.AppendData(identity);
            hasher.AppendData(payload.Span);
            commandHash = hasher.GetHashAndReset();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(identity);
        }

        try
        {
            return new SecretStoreResourceCommand(
                "trust-" + Convert.ToHexStringLower(commandHash),
                SecretStoreProtocol.TrustAddCommandKind,
                owner,
                issuer.Issuer,
                payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(commandHash);
        }
    }

    private static ReadOnlyMemory<byte> CreatePayload(TrustedIssuer issuer)
    {
        byte[] publicKey = Encoding.UTF8.GetBytes(issuer.PublicKey.GetRawText());
        if (issuer.AllowedCommandKinds is not { Count: > 0 } allowedCommandKinds)
        {
            return publicKey;
        }

        using JsonDocument document = JsonDocument.Parse(publicKey);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("trustKey");
            document.RootElement.WriteTo(writer);
            writer.WriteStartArray("allowedCommandKinds");
            foreach (string kind in allowedCommandKinds)
            {
                writer.WriteStringValue(kind);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenMemory.ToArray();
    }
}
