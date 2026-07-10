using System;
using System.Security.Cryptography;

namespace Assimalign.Cohesion.Web.Authentication.Bearer;

/// <summary>
/// Factory for the built-in <see cref="IJwtSignatureVerifier"/> implementations. Each verifier is
/// bound to concrete key material and, optionally, a <c>kid</c>, so a bearer scheme can register
/// several keys and let each token select the matching one.
/// </summary>
public static class JwtSignatureVerifier
{
    /// <summary>
    /// Creates a verifier for HMAC-signed tokens (<c>HS256</c>/<c>HS384</c>/<c>HS512</c>).
    /// </summary>
    /// <param name="key">The shared secret. Copied defensively.</param>
    /// <param name="keyId">The <c>kid</c> this key answers to, or <see langword="null"/> to match any token that omits or ignores <c>kid</c>.</param>
    /// <returns>An HMAC verifier.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty.</exception>
    public static IJwtSignatureVerifier CreateHmac(byte[] key, string? keyId = null)
        => new HmacJwtSignatureVerifier(key, keyId);

    /// <summary>
    /// Creates a verifier for RSA-signed tokens (<c>RS*</c> and <c>PS*</c>) with an RSA public key.
    /// </summary>
    /// <param name="publicKey">The RSA public key. The verifier borrows the instance; keep it alive for the scheme's lifetime.</param>
    /// <param name="keyId">The <c>kid</c> this key answers to, or <see langword="null"/> to match any <c>kid</c>.</param>
    /// <returns>An RSA verifier.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="publicKey"/> is <see langword="null"/>.</exception>
    public static IJwtSignatureVerifier CreateRsa(RSA publicKey, string? keyId = null)
        => new RsaJwtSignatureVerifier(publicKey, keyId);

    /// <summary>
    /// Creates a verifier for ECDSA-signed tokens (<c>ES256</c>/<c>ES384</c>/<c>ES512</c>) with an
    /// EC public key.
    /// </summary>
    /// <param name="publicKey">The EC public key. The verifier borrows the instance; keep it alive for the scheme's lifetime.</param>
    /// <param name="keyId">The <c>kid</c> this key answers to, or <see langword="null"/> to match any <c>kid</c>.</param>
    /// <returns>An ECDSA verifier.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="publicKey"/> is <see langword="null"/>.</exception>
    public static IJwtSignatureVerifier CreateEcdsa(ECDsa publicKey, string? keyId = null)
        => new EcdsaJwtSignatureVerifier(publicKey, keyId);
}
