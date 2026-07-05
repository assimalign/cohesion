namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Defines the well-known OpenID Connect endpoint kinds, as typed
/// <see cref="ProtocolEndpointKind" /> values per the family's vocabulary rule (the root
/// owns the vocabulary type; the owning branch owns the well-known values).
/// </summary>
public static class OpenIdConnectEndpointKinds
{
    /// <summary>
    /// Gets the authorization endpoint kind (<c>authorization_endpoint</c>).
    /// </summary>
    public static ProtocolEndpointKind Authorization { get; } = new("authorization");

    /// <summary>
    /// Gets the token endpoint kind (<c>token_endpoint</c>).
    /// </summary>
    public static ProtocolEndpointKind Token { get; } = new("token");

    /// <summary>
    /// Gets the UserInfo endpoint kind (<c>userinfo_endpoint</c>).
    /// </summary>
    public static ProtocolEndpointKind UserInfo { get; } = new("userinfo");

    /// <summary>
    /// Gets the JWK Set document endpoint kind (<c>jwks_uri</c>).
    /// </summary>
    public static ProtocolEndpointKind Jwks { get; } = new("jwks");

    /// <summary>
    /// Gets the dynamic client registration endpoint kind (<c>registration_endpoint</c>).
    /// </summary>
    public static ProtocolEndpointKind Registration { get; } = new("registration");

    /// <summary>
    /// Gets the RP-initiated logout endpoint kind (<c>end_session_endpoint</c>).
    /// </summary>
    public static ProtocolEndpointKind EndSession { get; } = new("end-session");

    /// <summary>
    /// Gets the client front-channel logout endpoint kind
    /// (<c>frontchannel_logout_uri</c>).
    /// </summary>
    public static ProtocolEndpointKind FrontChannelLogout { get; } = new("front-channel-logout");

    /// <summary>
    /// Gets the client back-channel logout endpoint kind (<c>backchannel_logout_uri</c>).
    /// </summary>
    public static ProtocolEndpointKind BackChannelLogout { get; } = new("back-channel-logout");
}
