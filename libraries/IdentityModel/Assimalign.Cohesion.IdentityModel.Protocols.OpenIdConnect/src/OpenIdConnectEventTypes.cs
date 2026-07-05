namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Defines the security event URIs used in the <c>events</c> claim.
/// </summary>
public static class OpenIdConnectEventTypes
{
    /// <summary>
    /// The back-channel logout event (Back-Channel Logout 1.0 §2.4). A logout token must
    /// carry this member in its <c>events</c> object, with an empty-object value.
    /// </summary>
    public const string BackChannelLogout = "http://schemas.openid.net/event/backchannel-logout";
}
