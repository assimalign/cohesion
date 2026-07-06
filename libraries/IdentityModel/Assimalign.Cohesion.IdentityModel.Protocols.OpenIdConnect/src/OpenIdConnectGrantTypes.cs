namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Defines the <c>grant_type</c> values relevant to OpenID Connect flows.
/// </summary>
public static class OpenIdConnectGrantTypes
{
    /// <summary>
    /// The authorization code grant (<c>authorization_code</c>).
    /// </summary>
    public const string AuthorizationCode = "authorization_code";

    /// <summary>
    /// The refresh token grant (<c>refresh_token</c>).
    /// </summary>
    public const string RefreshToken = "refresh_token";

    /// <summary>
    /// The client credentials grant (<c>client_credentials</c>). OAuth-only: it produces
    /// no ID token.
    /// </summary>
    public const string ClientCredentials = "client_credentials";

    /// <summary>
    /// The legacy implicit grant (<c>implicit</c>), represented only where compatibility
    /// requires it.
    /// </summary>
    public const string Implicit = "implicit";
}
