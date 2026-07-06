namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

/// <summary>
/// Defines the IANA-registered JWT claim names the JSON Web Token document carries beyond the
/// RFC 7519 registered set. The core registered names (<c>iss</c>, <c>sub</c>, <c>aud</c>,
/// <c>exp</c>, <c>iat</c>, <c>nbf</c>, <c>jti</c>) live on the canonical
/// <see cref="IdentityClaimTypes" /> and are used from there.
/// </summary>
/// <remarks>
/// These names are also declared by the OpenID Connect branch's <c>OpenIdConnectClaimTypes</c>.
/// The two are deliberate independent mirrors of the same IANA JWT Claims registry: the token
/// branch must not reference the protocol branch, so it owns the JWT document names it
/// materializes. A cross-branch test pins the string values equal so they cannot drift.
/// </remarks>
public static class JsonWebTokenClaimTypes
{
    /// <summary>The authentication instant claim (<c>auth_time</c>).</summary>
    public const string AuthTime = "auth_time";

    /// <summary>The replay-prevention nonce claim (<c>nonce</c>).</summary>
    public const string Nonce = "nonce";

    /// <summary>The authentication context class reference claim (<c>acr</c>).</summary>
    public const string AuthenticationContextClassReference = "acr";

    /// <summary>The authentication method references claim (<c>amr</c>).</summary>
    public const string AuthenticationMethodReferences = "amr";

    /// <summary>The authorized party claim (<c>azp</c>).</summary>
    public const string AuthorizedParty = "azp";

    /// <summary>The access token hash claim (<c>at_hash</c>).</summary>
    public const string AccessTokenHash = "at_hash";

    /// <summary>The code hash claim (<c>c_hash</c>).</summary>
    public const string CodeHash = "c_hash";

    /// <summary>The session identifier claim (<c>sid</c>).</summary>
    public const string SessionId = "sid";
}
