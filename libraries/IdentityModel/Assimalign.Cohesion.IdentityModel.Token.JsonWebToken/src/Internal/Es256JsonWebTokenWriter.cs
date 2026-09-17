using System;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

/// <summary>
/// Writes ES256 compact JWS values with an externally owned ECDSA P-256 private key.
/// </summary>
internal sealed class Es256JsonWebTokenWriter : IJsonWebTokenWriter
{
    private readonly ECDsa _privateKey;
    private readonly string _keyId;

    public Es256JsonWebTokenWriter(ECDsa privateKey, string keyId)
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

    /// <inheritdoc />
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
}
