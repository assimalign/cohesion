using System;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken.Internal;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

// Deviates from the internal-implementation rule per owner decision 59 (#1465): the implementation is public.
/// <summary>
/// Writes ES256 compact JWS values with an externally owned ECDSA P-256 private key.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("JsonWebTokenWriter")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class JsonWebTokenWriter : IJsonWebTokenWriter
{
    private readonly ECDsa _privateKey;
    private readonly string _keyId;

    private JsonWebTokenWriter(ECDsa privateKey, string keyId)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);

        if (!JsonWebTokenSignatureAlgorithms.TryGetEcdsaAlgorithm(privateKey, out string algorithm) ||
            algorithm != JoseAlgorithms.ES256)
        {
            throw new ArgumentException("ES256 requires an ECDSA key on the NIST P-256 curve.", nameof(privateKey));
        }

        _privateKey = privateKey;
        _keyId = keyId;
    }

    /// <summary>
    /// Creates an ES256 writer that borrows the supplied ECDSA P-256 private key.
    /// </summary>
    /// <param name="privateKey">
    /// The ECDSA P-256 private key. The writer does not dispose it; the caller must keep it
    /// alive for the writer's lifetime.
    /// </param>
    /// <param name="keyId">The non-empty <c>kid</c> written to every JOSE header.</param>
    /// <returns>An ES256 compact-token writer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="privateKey" /> or <paramref name="keyId" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="keyId" /> is empty, or <paramref name="privateKey" /> does not use the
    /// NIST P-256 curve.
    /// </exception>
    public static JsonWebTokenWriter CreateEs256(ECDsa privateKey, string keyId)
        => new JsonWebTokenWriter(privateKey, keyId);

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="descriptor"/> is <see langword="null"/>.</exception>
    public string Write(JsonWebTokenDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        string header = JsonWebTokenSerializer.EncodeHeader(descriptor, JoseAlgorithms.ES256, _keyId);
        string payload = JsonWebTokenSerializer.EncodePayload(descriptor);
        string signingInput = string.Concat(header, ".", payload);
        byte[] signingInputBytes = Encoding.ASCII.GetBytes(signingInput);

        byte[] signature = _privateKey.SignData(
            signingInputBytes,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return string.Concat(signingInput, ".", Base64Url.EncodeToString(signature));
    }

    private sealed class DebugView
    {
        public DebugView(JsonWebTokenWriter value)
        {
        }
    }
}
