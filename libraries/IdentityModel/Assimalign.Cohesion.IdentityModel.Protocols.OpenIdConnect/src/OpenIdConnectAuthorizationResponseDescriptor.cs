using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Describes the contents of an authorization response before it is materialized into an
/// immutable <see cref="OpenIdConnectAuthorizationResponse" />.
/// </summary>
/// <remarks>
/// The base envelope's <see cref="ProtocolMessageDescriptor.Issuer" /> carries the RFC
/// 9207 <c>iss</c> response parameter — set it only from wire data so absence detection
/// (mix-up defense) stays possible. Error responses map the wire error triplet onto
/// <see cref="ProtocolResponseDescriptor.Status" /> via
/// <see cref="ProtocolResponseStatus.Failed" />; statusless success responses set
/// <see cref="ProtocolResponseStatus.Success" /> explicitly.
/// </remarks>
public class OpenIdConnectAuthorizationResponseDescriptor : ProtocolResponseDescriptor
{
    /// <summary>
    /// Gets or sets the authorization code (<c>code</c>).
    /// </summary>
    public string? Code { get; set; }

    /// <summary>
    /// Gets or sets the ID token (<c>id_token</c>), as the raw compact string
    /// (implicit/hybrid compatibility).
    /// </summary>
    public string? IdToken { get; set; }

    /// <summary>
    /// Gets or sets the access token (<c>access_token</c>), as the raw string
    /// (implicit/hybrid compatibility).
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
    /// Gets the granted scopes (<c>scope</c>), when the server narrowed the request.
    /// </summary>
    public IList<string> Scopes { get; } = new List<string>();
}
