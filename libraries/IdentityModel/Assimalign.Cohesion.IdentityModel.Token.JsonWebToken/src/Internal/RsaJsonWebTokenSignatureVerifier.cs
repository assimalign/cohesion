using System;
using System.Security.Cryptography;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken.Internal;

/// <summary>
/// Verifies JOSE RSA PKCS#1 and RSA-PSS signatures.
/// </summary>
internal sealed class RsaJsonWebTokenSignatureVerifier : IJsonWebTokenSignatureVerifier
{
    private readonly RSA _publicKey;
    private readonly string? _keyId;

    public RsaJsonWebTokenSignatureVerifier(RSA publicKey, string? keyId)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        _publicKey = publicKey;
        _keyId = keyId;
    }

    /// <inheritdoc />
    public bool CanVerify(string algorithm, string? keyId)
        => (JsonWebTokenSignatureAlgorithms.IsRsaPkcs1(algorithm) ||
            JsonWebTokenSignatureAlgorithms.IsRsaPss(algorithm)) &&
           (_keyId is null || string.Equals(_keyId, keyId, StringComparison.Ordinal));

    /// <inheritdoc />
    public bool Verify(string algorithm, ReadOnlySpan<byte> signingInput, ReadOnlySpan<byte> signature)
    {
        if (!JsonWebTokenSignatureAlgorithms.IsRsaPkcs1(algorithm) &&
            !JsonWebTokenSignatureAlgorithms.IsRsaPss(algorithm))
        {
            return false;
        }

        HashAlgorithmName? hash = JsonWebTokenSignatureAlgorithms.GetHash(algorithm);
        if (hash is null)
        {
            return false;
        }

        RSASignaturePadding padding = JsonWebTokenSignatureAlgorithms.IsRsaPss(algorithm)
            ? RSASignaturePadding.Pss
            : RSASignaturePadding.Pkcs1;

        try
        {
            return _publicKey.VerifyData(signingInput, signature, hash.Value, padding);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
