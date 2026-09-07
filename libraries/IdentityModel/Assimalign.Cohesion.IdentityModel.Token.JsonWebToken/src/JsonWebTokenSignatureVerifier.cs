using System;
using System.Security.Cryptography;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

/// <summary>
/// Creates the built-in asymmetric JSON Web Token signature verifiers.
/// </summary>
public static class JsonWebTokenSignatureVerifier
{
    /// <summary>
    /// Creates a verifier for RSA PKCS#1 (<c>RS*</c>) and RSA-PSS (<c>PS*</c>) signatures.
    /// </summary>
    /// <param name="publicKey">
    /// The RSA verification key. The verifier does not dispose it; the caller must keep it
    /// alive for the verifier's lifetime.
    /// </param>
    /// <param name="keyId">The <c>kid</c> this key answers to, or <see langword="null" /> to match any key identifier.</param>
    /// <returns>An RSA JSON Web Token signature verifier.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="publicKey" /> is <see langword="null" />.</exception>
    public static IJsonWebTokenSignatureVerifier CreateRsa(RSA publicKey, string? keyId = null)
        => new RsaJsonWebTokenSignatureVerifier(publicKey, keyId);

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
    public static IJsonWebTokenSignatureVerifier CreateEcdsa(ECDsa publicKey, string? keyId = null)
        => new EcdsaJsonWebTokenSignatureVerifier(publicKey, keyId);
}
