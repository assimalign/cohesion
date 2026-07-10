using System;
using System.Security.Cryptography;

namespace Assimalign.Cohesion.Web.Authentication.Bearer;

/// <summary>
/// Verifies ECDSA-signed tokens (<c>ES256</c>/<c>ES384</c>/<c>ES512</c>) with an EC public key.
/// JWS carries the signature as the fixed-size IEEE P1363 concatenation of <c>r</c> and <c>s</c>,
/// which is passed through verbatim.
/// </summary>
internal sealed class EcdsaJwtSignatureVerifier : IJwtSignatureVerifier
{
    private readonly ECDsa _publicKey;
    private readonly string? _keyId;

    public EcdsaJwtSignatureVerifier(ECDsa publicKey, string? keyId)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        _publicKey = publicKey;
        _keyId = keyId;
    }

    /// <inheritdoc />
    public bool CanVerify(string algorithm, string? keyId)
    {
        if (!JwtSignatureAlgorithms.IsEcdsa(algorithm))
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

        try
        {
            // JWS ECDSA signatures are the raw r||s concatenation (IEEE P1363), not DER.
            return _publicKey.VerifyData(signingInput, signature, hash.Value, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
