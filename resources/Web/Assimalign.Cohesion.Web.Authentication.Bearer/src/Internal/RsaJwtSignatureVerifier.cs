using System;
using System.Security.Cryptography;

namespace Assimalign.Cohesion.Web.Authentication.Bearer;

/// <summary>
/// Verifies RSA-signed tokens with an RSA public key. Supports both PKCS#1 v1.5
/// (<c>RS256</c>/<c>RS384</c>/<c>RS512</c>) and PSS (<c>PS256</c>/<c>PS384</c>/<c>PS512</c>); the
/// padding is selected from the token's <c>alg</c>.
/// </summary>
internal sealed class RsaJwtSignatureVerifier : IJwtSignatureVerifier
{
    private readonly RSA _publicKey;
    private readonly string? _keyId;

    public RsaJwtSignatureVerifier(RSA publicKey, string? keyId)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        _publicKey = publicKey;
        _keyId = keyId;
    }

    /// <inheritdoc />
    public bool CanVerify(string algorithm, string? keyId)
    {
        if (!JwtSignatureAlgorithms.IsRsaPkcs1(algorithm) && !JwtSignatureAlgorithms.IsRsaPss(algorithm))
        {
            return false;
        }

        return _keyId is null || string.Equals(_keyId, keyId, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public bool Verify(string algorithm, ReadOnlySpan<byte> signingInput, ReadOnlySpan<byte> signature)
    {
        HashAlgorithmName? hash = JwtSignatureAlgorithms.GetHash(algorithm);
        if (hash is null)
        {
            return false;
        }

        RSASignaturePadding padding = JwtSignatureAlgorithms.IsRsaPss(algorithm)
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
