namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Defines the scope values registered by OpenID Connect Core 1.0.
/// </summary>
public static class OpenIdConnectScopes
{
    /// <summary>
    /// The scope that makes an OAuth 2.0 request an OpenID Connect request
    /// (<c>openid</c>). Core §3.1.2.1 requires it on every authentication request.
    /// </summary>
    public const string OpenId = "openid";

    /// <summary>
    /// The profile claims scope (<c>profile</c>).
    /// </summary>
    public const string Profile = "profile";

    /// <summary>
    /// The email claims scope (<c>email</c>).
    /// </summary>
    public const string Email = "email";

    /// <summary>
    /// The address claim scope (<c>address</c>).
    /// </summary>
    public const string Address = "address";

    /// <summary>
    /// The phone claims scope (<c>phone</c>).
    /// </summary>
    public const string Phone = "phone";

    /// <summary>
    /// The refresh-token consent scope (<c>offline_access</c>).
    /// </summary>
    public const string OfflineAccess = "offline_access";
}
