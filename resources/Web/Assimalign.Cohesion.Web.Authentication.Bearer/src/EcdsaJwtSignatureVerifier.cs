using System;
using System.Security.Cryptography;

using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.Web.Authentication.Bearer;

// Deviates from the internal-implementation rule per owner decision 59 (#1465): the implementation is public.
/// <summary>
/// Adapts the reusable IdentityModel ECDSA verifier to Bearer's existing public seam.
/// </summary>
public sealed class EcdsaJwtSignatureVerifier : IJwtSignatureVerifier
{
    private readonly IJsonWebTokenSignatureVerifier _verifier;

    private EcdsaJwtSignatureVerifier(IJsonWebTokenSignatureVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        _verifier = verifier;
    }

    /// <summary>
    /// Creates a verifier for ECDSA-signed tokens (<c>ES256</c>/<c>ES384</c>/<c>ES512</c>) with an
    /// EC public key.
    /// </summary>
    /// <param name="publicKey">The EC public key. The verifier borrows the instance; keep it alive for the scheme's lifetime.</param>
    /// <param name="keyId">The <c>kid</c> this key answers to, or <see langword="null"/> to match any <c>kid</c>.</param>
    /// <returns>An ECDSA verifier.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="publicKey"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="publicKey"/> does not use a NIST curve defined by a JOSE ECDSA algorithm.
    /// </exception>
    public static EcdsaJwtSignatureVerifier Create(ECDsa publicKey, string? keyId = null)
        => new EcdsaJwtSignatureVerifier(EcdsaJsonWebTokenSignatureVerifier.Create(publicKey, keyId));

    /// <inheritdoc />
    public bool CanVerify(string algorithm, string? keyId) => _verifier.CanVerify(algorithm, keyId);

    /// <inheritdoc />
    public bool Verify(string algorithm, ReadOnlySpan<byte> signingInput, ReadOnlySpan<byte> signature)
        => _verifier.Verify(algorithm, signingInput, signature);
}
