namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

/// <summary>
/// Defines the JWT-minted validation diagnostic codes. Cross-cutting concepts (issuer,
/// audience, temporal, missing required member) use the shared
/// <see cref="TokenValidationCodes" />; this class carries only diagnostics the JOSE/JWT
/// document layer itself defines, mirroring how the OpenID Connect branch mints
/// <c>OpenIdConnectValidationCodes</c>.
/// </summary>
public static class JsonWebTokenValidationCodes
{
    /// <summary>The token uses the unsecured <c>none</c> algorithm and it was not allowed (RFC 8725).</summary>
    public const string AlgorithmNone = "algorithm_none";

    /// <summary>The token's algorithm is not in the allowed set, or has no defined hash for a required hash check.</summary>
    public const string UnsupportedAlgorithm = "unsupported_algorithm";

    /// <summary>The computed access-token hash does not match the token's <c>at_hash</c> claim.</summary>
    public const string AccessTokenHashMismatch = "at_hash_mismatch";

    /// <summary>The computed authorization-code hash does not match the token's <c>c_hash</c> claim.</summary>
    public const string CodeHashMismatch = "c_hash_mismatch";

    /// <summary>A token hash claim (<c>at_hash</c>/<c>c_hash</c>) is absent although the value to hash was supplied.</summary>
    public const string TokenHashMissing = "token_hash_missing";

    /// <summary>The header lists a critical parameter (<c>crit</c>) the caller did not declare understood.</summary>
    public const string UnrecognizedCriticalHeader = "unrecognized_critical_header";

    /// <summary>The header selects the unencoded-payload variant (<c>b64:false</c>), which this package does not support.</summary>
    public const string UnsupportedBase64Payload = "unsupported_b64_payload";
}
