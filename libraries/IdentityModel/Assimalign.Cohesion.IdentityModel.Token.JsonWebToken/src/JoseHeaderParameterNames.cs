namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

/// <summary>
/// Defines the JOSE header parameter names (RFC 7515 §4.1, plus <c>b64</c> from RFC 7797).
/// </summary>
public static class JoseHeaderParameterNames
{
    /// <summary>The signing algorithm parameter (<c>alg</c>).</summary>
    public const string Algorithm = "alg";

    /// <summary>The token type parameter (<c>typ</c>).</summary>
    public const string Type = "typ";

    /// <summary>The content type parameter (<c>cty</c>).</summary>
    public const string ContentType = "cty";

    /// <summary>The key identifier parameter (<c>kid</c>).</summary>
    public const string KeyId = "kid";

    /// <summary>The JWK Set URL parameter (<c>jku</c>).</summary>
    public const string JwkSetUrl = "jku";

    /// <summary>The JSON Web Key parameter (<c>jwk</c>).</summary>
    public const string JsonWebKey = "jwk";

    /// <summary>The X.509 URL parameter (<c>x5u</c>).</summary>
    public const string X509Url = "x5u";

    /// <summary>The X.509 certificate chain parameter (<c>x5c</c>).</summary>
    public const string X509CertificateChain = "x5c";

    /// <summary>The X.509 certificate SHA-1 thumbprint parameter (<c>x5t</c>).</summary>
    public const string X509Thumbprint = "x5t";

    /// <summary>The X.509 certificate SHA-256 thumbprint parameter (<c>x5t#S256</c>).</summary>
    public const string X509ThumbprintSha256 = "x5t#S256";

    /// <summary>The critical header parameters list (<c>crit</c>).</summary>
    public const string Critical = "crit";

    /// <summary>The base64url payload flag parameter (<c>b64</c>, RFC 7797).</summary>
    public const string Base64Payload = "b64";
}
