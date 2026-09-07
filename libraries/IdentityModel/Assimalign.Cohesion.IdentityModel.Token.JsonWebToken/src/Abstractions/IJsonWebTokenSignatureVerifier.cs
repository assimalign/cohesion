using System;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

/// <summary>
/// Verifies an asymmetric signature over a JSON Web Token's exact JWS signing input.
/// </summary>
/// <remarks>
/// A verifier is bound to concrete RSA or ECDSA key material and optionally to a key
/// identifier. That key-family binding prevents an asymmetric key from being used as a
/// symmetric secret during algorithm-confusion attacks.
/// </remarks>
public interface IJsonWebTokenSignatureVerifier
{
    /// <summary>
    /// Determines whether this verifier can verify a token signed with the specified algorithm
    /// and key identifier.
    /// </summary>
    /// <param name="algorithm">The token's <c>alg</c> header value.</param>
    /// <param name="keyId">The token's <c>kid</c> header value, or <see langword="null" /> when absent.</param>
    /// <returns><see langword="true" /> when this verifier should attempt verification; otherwise <see langword="false" />.</returns>
    bool CanVerify(string algorithm, string? keyId);

    /// <summary>
    /// Verifies decoded signature octets over the exact ASCII <c>header.payload</c> signing
    /// input.
    /// </summary>
    /// <param name="algorithm">The token's <c>alg</c> header value.</param>
    /// <param name="signingInput">The exact JWS signing-input octets.</param>
    /// <param name="signature">The decoded signature octets.</param>
    /// <returns><see langword="true" /> when the signature is valid; otherwise <see langword="false" />.</returns>
    bool Verify(string algorithm, ReadOnlySpan<byte> signingInput, ReadOnlySpan<byte> signature);
}
