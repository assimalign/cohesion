using System;
using System.Security.Cryptography;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken.Internal;

/// <summary>
/// Verifies JOSE ECDSA signatures in their fixed-size IEEE P1363 <c>r||s</c> representation.
/// </summary>
internal sealed class EcdsaJsonWebTokenSignatureVerifier : IJsonWebTokenSignatureVerifier
{
    private readonly ECDsa _publicKey;
    private readonly string? _keyId;
    private readonly string _algorithm;

    public EcdsaJsonWebTokenSignatureVerifier(ECDsa publicKey, string? keyId)
    {
        ArgumentNullException.ThrowIfNull(publicKey);

        if (!JsonWebTokenSignatureAlgorithms.TryGetEcdsaAlgorithm(publicKey, out string algorithm))
        {
            throw new ArgumentException(
                "The ECDSA key must use the NIST P-256, P-384, or P-521 curve for a JOSE ES* algorithm.",
                nameof(publicKey));
        }

        _publicKey = publicKey;
        _keyId = keyId;
        _algorithm = algorithm;
    }

    /// <inheritdoc />
    public bool CanVerify(string algorithm, string? keyId)
        => algorithm == _algorithm &&
           (_keyId is null || string.Equals(_keyId, keyId, StringComparison.Ordinal));

    /// <inheritdoc />
    public bool Verify(string algorithm, ReadOnlySpan<byte> signingInput, ReadOnlySpan<byte> signature)
    {
        if (algorithm != _algorithm ||
            signature.Length != JsonWebTokenSignatureAlgorithms.GetEcdsaSignatureSize(algorithm))
        {
            return false;
        }

        HashAlgorithmName? hash = JsonWebTokenSignatureAlgorithms.GetHash(algorithm);
        if (hash is null)
        {
            return false;
        }

        try
        {
            return _publicKey.VerifyData(
                signingInput,
                signature,
                hash.Value,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
