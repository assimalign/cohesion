using System;
using System.Security.Cryptography;

using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken.Internal;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

// Deviates from the internal-implementation rule per owner decision 59 (#1465): the implementation is public.
/// <summary>
/// Verifies JOSE ECDSA signatures in their fixed-size IEEE P1363 <c>r||s</c> representation.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("EcdsaJsonWebTokenSignatureVerifier")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class EcdsaJsonWebTokenSignatureVerifier : IJsonWebTokenSignatureVerifier
{
    private readonly ECDsa _publicKey;
    private readonly string? _keyId;
    private readonly string _algorithm;

    private EcdsaJsonWebTokenSignatureVerifier(ECDsa publicKey, string? keyId)
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

    /// <summary>
    /// Creates a verifier for ECDSA signatures, binding ES256 to NIST P-256, ES384 to NIST
    /// P-384, and ES512 to NIST P-521.
    /// </summary>
    /// <param name="publicKey">
    /// The ECDSA verification key. The verifier does not dispose it; the caller must keep it
    /// alive for the verifier's lifetime.
    /// </param>
    /// <param name="keyId">The <c>kid</c> this key answers to, or <see langword="null" /> to match any key identifier.</param>
    /// <returns>An ECDSA JSON Web Token signature verifier.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="publicKey" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="publicKey" /> does not use a NIST curve defined by the JOSE ECDSA
    /// algorithms.
    /// </exception>
    public static EcdsaJsonWebTokenSignatureVerifier Create(ECDsa publicKey, string? keyId = null)
        => new EcdsaJsonWebTokenSignatureVerifier(publicKey, keyId);

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

    private sealed class DebugView
    {
        public DebugView(EcdsaJsonWebTokenSignatureVerifier value)
        {
        }
    }
}
