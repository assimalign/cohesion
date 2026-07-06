using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Describes the contents of a token endpoint request before it is materialized into an
/// immutable <see cref="OpenIdConnectTokenRequest" />.
/// </summary>
/// <remarks>
/// Client secrets are never modeled: <c>client_secret</c> for basic or post
/// authentication is injected by the transport layer. Assertion-based client
/// authentication (<c>private_key_jwt</c>) is representable because a client assertion is
/// a signed public artifact, not a secret.
/// </remarks>
public class OpenIdConnectTokenRequestDescriptor : ProtocolRequestDescriptor
{
    /// <summary>
    /// Gets or sets the client identifier (<c>client_id</c>). Alias of the base
    /// envelope's <see cref="ProtocolMessageDescriptor.Issuer" />.
    /// </summary>
    public string? ClientId
    {
        get => Issuer;
        set => Issuer = value;
    }

    /// <summary>
    /// Gets or sets the grant type (<c>grant_type</c>). Required at materialization.
    /// </summary>
    public string? GrantType { get; set; }

    /// <summary>
    /// Gets or sets the authorization code (<c>code</c>).
    /// </summary>
    public string? Code { get; set; }

    /// <summary>
    /// Gets or sets the redirect URI the code was issued to (<c>redirect_uri</c>).
    /// </summary>
    public string? RedirectUri { get; set; }

    /// <summary>
    /// Gets or sets the PKCE code verifier (<c>code_verifier</c>).
    /// </summary>
    public string? CodeVerifier { get; set; }

    /// <summary>
    /// Gets or sets the refresh token (<c>refresh_token</c>).
    /// </summary>
    public string? RefreshToken { get; set; }

    /// <summary>
    /// Gets the requested scopes (<c>scope</c>).
    /// </summary>
    public IList<string> Scopes { get; } = new List<string>();

    /// <summary>
    /// Gets or sets the client assertion (<c>client_assertion</c>, RFC 7523), as the raw
    /// compact JWT.
    /// </summary>
    public string? ClientAssertion { get; set; }

    /// <summary>
    /// Gets or sets the client assertion type (<c>client_assertion_type</c>).
    /// </summary>
    public string? ClientAssertionType { get; set; }
}
