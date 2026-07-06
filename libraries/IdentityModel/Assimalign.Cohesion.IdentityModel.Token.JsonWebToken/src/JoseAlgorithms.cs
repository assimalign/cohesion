namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

/// <summary>
/// Defines the well-known JOSE signature algorithm identifiers (RFC 7518 §3.1), including the
/// unsecured <see cref="None" /> algorithm that RFC 8725 requires be rejected by default.
/// </summary>
public static class JoseAlgorithms
{
    /// <summary>The unsecured (no signature) algorithm (<c>none</c>).</summary>
    public const string None = "none";

    /// <summary>HMAC using SHA-256 (<c>HS256</c>).</summary>
    public const string HS256 = "HS256";

    /// <summary>HMAC using SHA-384 (<c>HS384</c>).</summary>
    public const string HS384 = "HS384";

    /// <summary>HMAC using SHA-512 (<c>HS512</c>).</summary>
    public const string HS512 = "HS512";

    /// <summary>RSASSA-PKCS1-v1_5 using SHA-256 (<c>RS256</c>).</summary>
    public const string RS256 = "RS256";

    /// <summary>RSASSA-PKCS1-v1_5 using SHA-384 (<c>RS384</c>).</summary>
    public const string RS384 = "RS384";

    /// <summary>RSASSA-PKCS1-v1_5 using SHA-512 (<c>RS512</c>).</summary>
    public const string RS512 = "RS512";

    /// <summary>ECDSA using P-256 and SHA-256 (<c>ES256</c>).</summary>
    public const string ES256 = "ES256";

    /// <summary>ECDSA using P-384 and SHA-384 (<c>ES384</c>).</summary>
    public const string ES384 = "ES384";

    /// <summary>ECDSA using P-521 and SHA-512 (<c>ES512</c>).</summary>
    public const string ES512 = "ES512";

    /// <summary>RSASSA-PSS using SHA-256 (<c>PS256</c>).</summary>
    public const string PS256 = "PS256";

    /// <summary>RSASSA-PSS using SHA-384 (<c>PS384</c>).</summary>
    public const string PS384 = "PS384";

    /// <summary>RSASSA-PSS using SHA-512 (<c>PS512</c>).</summary>
    public const string PS512 = "PS512";
}
