using System;

namespace Assimalign.Cohesion.Web.Authentication.Bearer;

/// <summary>
/// Verifies the signature of a JSON Web Token. This is the keyed cryptographic seam the bearer
/// handler consumes: the IdentityModel JSON Web Token package deliberately validates only the
/// document-level rules (issuer, audience, lifetime, algorithm) and leaves signature verification
/// to a key-holding component — this interface is that component.
/// </summary>
/// <remarks>
/// Implementations are keyed by concrete key material (an HMAC secret, an RSA or ECDSA public key),
/// so the algorithm a verifier accepts is bounded by its key type. That binding is itself a defense
/// against algorithm-confusion: a token whose <c>alg</c> was swapped to a symmetric algorithm finds
/// no asymmetric verifier willing to accept it.
/// </remarks>
public interface IJwtSignatureVerifier
{
    /// <summary>
    /// Determines whether this verifier can verify a token signed with <paramref name="algorithm"/>
    /// and identified by <paramref name="keyId"/>.
    /// </summary>
    /// <param name="algorithm">The token's <c>alg</c> header value (for example <c>RS256</c>).</param>
    /// <param name="keyId">The token's <c>kid</c> header value, or <see langword="null"/> when absent.</param>
    /// <returns><see langword="true"/> when this verifier should attempt verification; otherwise <see langword="false"/>.</returns>
    bool CanVerify(string algorithm, string? keyId);

    /// <summary>
    /// Verifies a signature over the JWS signing input.
    /// </summary>
    /// <param name="algorithm">The token's <c>alg</c> header value.</param>
    /// <param name="signingInput">The ASCII octets of <c>header.payload</c> exactly as received.</param>
    /// <param name="signature">The decoded signature octets.</param>
    /// <returns><see langword="true"/> when the signature is valid; otherwise <see langword="false"/>.</returns>
    bool Verify(string algorithm, ReadOnlySpan<byte> signingInput, ReadOnlySpan<byte> signature);
}
