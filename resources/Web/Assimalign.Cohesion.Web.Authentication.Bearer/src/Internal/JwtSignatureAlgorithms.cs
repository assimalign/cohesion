using System;
using System.Security.Cryptography;

using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.Web.Authentication.Bearer;

/// <summary>
/// Maps JOSE signature algorithm identifiers (RFC 7518) to the BCL hash they use and classifies
/// them by key family. Kept in one place so every built-in verifier agrees on the mapping.
/// </summary>
internal static class JwtSignatureAlgorithms
{
    public static bool IsHmac(string algorithm)
        => algorithm is JoseAlgorithms.HS256 or JoseAlgorithms.HS384 or JoseAlgorithms.HS512;

    public static bool IsRsaPkcs1(string algorithm)
        => algorithm is JoseAlgorithms.RS256 or JoseAlgorithms.RS384 or JoseAlgorithms.RS512;

    public static bool IsRsaPss(string algorithm)
        => algorithm is JoseAlgorithms.PS256 or JoseAlgorithms.PS384 or JoseAlgorithms.PS512;

    public static bool IsEcdsa(string algorithm)
        => algorithm is JoseAlgorithms.ES256 or JoseAlgorithms.ES384 or JoseAlgorithms.ES512;

    /// <summary>
    /// Resolves the hash algorithm for a JOSE signature algorithm, or <see langword="null"/> when
    /// the algorithm is unknown.
    /// </summary>
    public static HashAlgorithmName? GetHash(string algorithm) => algorithm switch
    {
        JoseAlgorithms.HS256 or JoseAlgorithms.RS256 or JoseAlgorithms.PS256 or JoseAlgorithms.ES256 => HashAlgorithmName.SHA256,
        JoseAlgorithms.HS384 or JoseAlgorithms.RS384 or JoseAlgorithms.PS384 or JoseAlgorithms.ES384 => HashAlgorithmName.SHA384,
        JoseAlgorithms.HS512 or JoseAlgorithms.RS512 or JoseAlgorithms.PS512 or JoseAlgorithms.ES512 => HashAlgorithmName.SHA512,
        _ => null,
    };
}
