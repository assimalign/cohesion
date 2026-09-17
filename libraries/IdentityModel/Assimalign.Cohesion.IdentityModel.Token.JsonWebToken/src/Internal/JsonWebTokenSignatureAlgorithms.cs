using System;
using System.Security.Cryptography;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

/// <summary>
/// Maps asymmetric JOSE algorithms to their BCL hash and key-family requirements.
/// </summary>
internal static class JsonWebTokenSignatureAlgorithms
{
    private const string NistP256Oid = "1.2.840.10045.3.1.7";
    private const string NistP384Oid = "1.3.132.0.34";
    private const string NistP521Oid = "1.3.132.0.35";

    public static bool IsRsaPkcs1(string algorithm)
        => algorithm is JoseAlgorithms.RS256 or JoseAlgorithms.RS384 or JoseAlgorithms.RS512;

    public static bool IsRsaPss(string algorithm)
        => algorithm is JoseAlgorithms.PS256 or JoseAlgorithms.PS384 or JoseAlgorithms.PS512;

    public static bool TryGetEcdsaAlgorithm(ECDsa key, out string algorithm)
    {
        try
        {
            string? curveOid = key.ExportParameters(includePrivateParameters: false).Curve.Oid.Value;
            string? resolvedAlgorithm = curveOid switch
            {
                NistP256Oid => JoseAlgorithms.ES256,
                NistP384Oid => JoseAlgorithms.ES384,
                NistP521Oid => JoseAlgorithms.ES512,
                _ => null,
            };

            algorithm = resolvedAlgorithm ?? string.Empty;
            return resolvedAlgorithm is not null;
        }
        catch (CryptographicException)
        {
            algorithm = string.Empty;
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            algorithm = string.Empty;
            return false;
        }
    }

    public static int GetEcdsaSignatureSize(string algorithm) => algorithm switch
    {
        JoseAlgorithms.ES256 => 64,
        JoseAlgorithms.ES384 => 96,
        JoseAlgorithms.ES512 => 132,
        _ => 0,
    };

    public static HashAlgorithmName? GetHash(string algorithm) => algorithm switch
    {
        JoseAlgorithms.RS256 or JoseAlgorithms.PS256 or JoseAlgorithms.ES256 => HashAlgorithmName.SHA256,
        JoseAlgorithms.RS384 or JoseAlgorithms.PS384 or JoseAlgorithms.ES384 => HashAlgorithmName.SHA384,
        JoseAlgorithms.RS512 or JoseAlgorithms.PS512 or JoseAlgorithms.ES512 => HashAlgorithmName.SHA512,
        _ => null,
    };
}
