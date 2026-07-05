using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Describes the contents of an RP-initiated logout request before it is materialized
/// into an immutable <see cref="OpenIdConnectLogoutRequest" />.
/// </summary>
public class OpenIdConnectLogoutRequestDescriptor : ProtocolLogoutRequestDescriptor
{
    /// <summary>
    /// Gets or sets the client identifier (<c>client_id</c>). Alias of the base
    /// envelope's <see cref="ProtocolMessageDescriptor.Issuer" /> — the relying party is
    /// the sender of an RP-initiated logout.
    /// </summary>
    public string? ClientId
    {
        get => Issuer;
        set => Issuer = value;
    }

    /// <summary>
    /// Gets or sets the ID token hint (<c>id_token_hint</c>), as the original compact
    /// token issued at login.
    /// </summary>
    public string? IdTokenHint { get; set; }

    /// <summary>
    /// Gets or sets the logout hint (<c>logout_hint</c>) — a hint about <em>who</em> to
    /// log out, distinct from the base envelope's <c>Reason</c> (which is <em>why</em>).
    /// </summary>
    public string? LogoutHint { get; set; }

    /// <summary>
    /// Gets or sets the post-logout redirect URI (<c>post_logout_redirect_uri</c>), as
    /// the exact wire string.
    /// </summary>
    public string? PostLogoutRedirectUri { get; set; }

    /// <summary>
    /// Gets the preferred UI locales (<c>ui_locales</c>).
    /// </summary>
    public IList<string> UiLocales { get; } = new List<string>();
}
