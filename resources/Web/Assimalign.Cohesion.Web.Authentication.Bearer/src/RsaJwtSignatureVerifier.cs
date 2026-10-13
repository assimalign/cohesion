using System;
using System.Security.Cryptography;

using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.Web.Authentication.Bearer;

// Deviates from the internal-implementation rule per owner decision 59 (#1465): the implementation is public.
/// <summary>
/// Adapts the reusable IdentityModel RSA verifier to Bearer's existing public seam.
/// </summary>
public sealed class RsaJwtSignatureVerifier : IJwtSignatureVerifier
{
    private readonly IJsonWebTokenSignatureVerifier _verifier;

    private RsaJwtSignatureVerifier(IJsonWebTokenSignatureVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        _verifier = verifier;
    }

    /// <summary>
    /// Creates a verifier for RSA-signed tokens (<c>RS*</c> and <c>PS*</c>) with an RSA public key.
    /// </summary>
    /// <param name="publicKey">The RSA public key. The verifier borrows the instance; keep it alive for the scheme's lifetime.</param>
    /// <param name="keyId">The <c>kid</c> this key answers to, or <see langword="null"/> to match any <c>kid</c>.</param>
    /// <returns>An RSA verifier.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="publicKey"/> is <see langword="null"/>.</exception>
    public static RsaJwtSignatureVerifier Create(RSA publicKey, string? keyId = null)
        => new RsaJwtSignatureVerifier(RsaJsonWebTokenSignatureVerifier.Create(publicKey, keyId));

    /// <inheritdoc />
    public bool CanVerify(string algorithm, string? keyId) => _verifier.CanVerify(algorithm, keyId);

    /// <inheritdoc />
    public bool Verify(string algorithm, ReadOnlySpan<byte> signingInput, ReadOnlySpan<byte> signature)
        => _verifier.Verify(algorithm, signingInput, signature);
}
