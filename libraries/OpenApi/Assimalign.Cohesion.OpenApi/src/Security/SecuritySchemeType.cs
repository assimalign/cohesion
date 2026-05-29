namespace Assimalign.Cohesion.OpenApi;

/// <summary>
/// The type of a security scheme. See the <c>type</c> field of the "Security Scheme Object" section of
/// the OpenAPI Specification.
/// </summary>
public enum SecuritySchemeType
{
    /// <summary>An API key passed in a header, query parameter, or cookie.</summary>
    ApiKey,

    /// <summary>HTTP authentication as defined by RFC 7235.</summary>
    Http,

    /// <summary>Mutual TLS client-certificate authentication (OpenAPI 3.1+).</summary>
    MutualTLS,

    /// <summary>OAuth 2.0 authentication.</summary>
    OAuth2,

    /// <summary>OpenID Connect Discovery.</summary>
    OpenIdConnect
}
