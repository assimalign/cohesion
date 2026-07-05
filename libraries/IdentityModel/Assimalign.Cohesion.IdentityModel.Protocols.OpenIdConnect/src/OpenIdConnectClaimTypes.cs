namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Defines the claim types minted by OpenID Connect. JWT-core registered names
/// (<c>iss</c>, <c>sub</c>, <c>aud</c>, <c>exp</c>, <c>iat</c>, <c>nbf</c>, <c>jti</c>)
/// live on the canonical <see cref="IdentityClaimTypes" /> registry; this class carries
/// only the claims OpenID Connect itself defines.
/// </summary>
public static class OpenIdConnectClaimTypes
{
    /// <summary>
    /// The authentication instant claim (<c>auth_time</c>).
    /// </summary>
    public const string AuthTime = "auth_time";

    /// <summary>
    /// The replay-prevention nonce claim (<c>nonce</c>).
    /// </summary>
    public const string Nonce = "nonce";

    /// <summary>
    /// The authentication context class reference claim (<c>acr</c>).
    /// </summary>
    public const string Acr = "acr";

    /// <summary>
    /// The authentication method references claim (<c>amr</c>).
    /// </summary>
    public const string Amr = "amr";

    /// <summary>
    /// The authorized party claim (<c>azp</c>).
    /// </summary>
    public const string Azp = "azp";

    /// <summary>
    /// The access token hash claim (<c>at_hash</c>).
    /// </summary>
    public const string AccessTokenHash = "at_hash";

    /// <summary>
    /// The code hash claim (<c>c_hash</c>).
    /// </summary>
    public const string CodeHash = "c_hash";

    /// <summary>
    /// The session identifier claim (<c>sid</c>).
    /// </summary>
    public const string SessionId = "sid";

    /// <summary>
    /// The security events claim (<c>events</c>).
    /// </summary>
    public const string Events = "events";
}
