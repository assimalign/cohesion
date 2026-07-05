using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Describes the contents of a token endpoint response before it is materialized into an
/// immutable <see cref="OpenIdConnectTokenResponse" />.
/// </summary>
public class OpenIdConnectTokenResponseDescriptor : ProtocolResponseDescriptor
{
    /// <summary>
    /// Gets or sets the access token (<c>access_token</c>), as the raw string.
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>
    /// Gets or sets the access token type (<c>token_type</c>).
    /// </summary>
    public string? TokenType { get; set; }

    /// <summary>
    /// Gets or sets the access token lifetime in seconds (<c>expires_in</c>).
    /// </summary>
    public long? ExpiresIn { get; set; }

    /// <summary>
    /// Gets or sets the refresh token (<c>refresh_token</c>).
    /// </summary>
    public string? RefreshToken { get; set; }

    /// <summary>
    /// Gets or sets the ID token (<c>id_token</c>), as the raw compact string.
    /// </summary>
    public string? IdToken { get; set; }

    /// <summary>
    /// Gets the granted scopes (<c>scope</c>), when the server narrowed the request.
    /// </summary>
    public IList<string> Scopes { get; } = new List<string>();
}
