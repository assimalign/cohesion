using System;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

/// <summary>
/// Computes the OpenID Connect access-token and code hash (<c>at_hash</c> / <c>c_hash</c>,
/// OIDC Core §3.1.3.6 / §3.3.2.11): the base64url encoding of the left-most half of the SHA-2
/// digest — sized by the JWS algorithm — of the token's ASCII octets. This is <em>keyless</em>
/// hashing (no key material, no algorithm-suite dispatch beyond reading the digest size from
/// the algorithm name), so it is document self-consistency, not the keyed cryptography the
/// family defers to the Security layer.
/// </summary>
internal static class TokenHashComputer
{
    /// <summary>
    /// Computes the expected hash for the given algorithm and token value, or
    /// <see langword="null" /> when the algorithm has no defined SHA-2 pairing (<c>none</c>,
    /// EdDSA, or an unrecognized algorithm).
    /// </summary>
    public static string? ComputeHalfHash(string algorithm, string value)
    {
        var digestSize = DigestSizeFor(algorithm);
        if (digestSize == 0)
        {
            return null;
        }

        var octets = Encoding.ASCII.GetBytes(value);
        var digest = digestSize switch
        {
            256 => SHA256.HashData(octets),
            384 => SHA384.HashData(octets),
            512 => SHA512.HashData(octets),
            _ => Array.Empty<byte>(),
        };

        // The hash is the base64url of the LEFT-MOST HALF of the digest.
        return Base64Url.EncodeToString(digest.AsSpan(0, digest.Length / 2));
    }

    /// <summary>
    /// Gets a value indicating whether the algorithm has a defined at_hash/c_hash SHA-2
    /// pairing.
    /// </summary>
    public static bool HasDefinedHash(string algorithm) => DigestSizeFor(algorithm) != 0;

    private static int DigestSizeFor(string? algorithm) => algorithm switch
    {
        JoseAlgorithms.HS256 or JoseAlgorithms.RS256 or JoseAlgorithms.ES256 or JoseAlgorithms.PS256 => 256,
        JoseAlgorithms.HS384 or JoseAlgorithms.RS384 or JoseAlgorithms.ES384 or JoseAlgorithms.PS384 => 384,
        JoseAlgorithms.HS512 or JoseAlgorithms.RS512 or JoseAlgorithms.ES512 or JoseAlgorithms.PS512 => 512,
        _ => 0,
    };
}
