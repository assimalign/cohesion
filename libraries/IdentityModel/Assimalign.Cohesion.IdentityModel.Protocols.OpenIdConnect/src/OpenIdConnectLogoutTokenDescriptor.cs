using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Describes the claim surface of a back-channel logout token before it is materialized
/// into an immutable <see cref="OpenIdConnectLogoutToken" />.
/// </summary>
public class OpenIdConnectLogoutTokenDescriptor
{
    /// <summary>
    /// Gets or sets the issuer (<c>iss</c>).
    /// </summary>
    public string? Issuer { get; set; }

    /// <summary>
    /// Gets or sets the subject (<c>sub</c>), as the raw wire string. A logout token may
    /// identify the session by <c>sid</c> alone.
    /// </summary>
    public string? Subject { get; set; }

    /// <summary>
    /// Gets the audiences (<c>aud</c>).
    /// </summary>
    public IList<string> Audiences { get; } = new List<string>();

    /// <summary>
    /// Gets or sets the issuance instant (<c>iat</c>).
    /// </summary>
    public DateTimeOffset? IssuedAt { get; set; }

    /// <summary>
    /// Gets or sets the expiration instant (<c>exp</c>).
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// Gets or sets the token identifier (<c>jti</c>).
    /// </summary>
    public string? JwtId { get; set; }

    /// <summary>
    /// Gets or sets the provider session identifier (<c>sid</c>).
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// Gets the security events (<c>events</c>) as the wire's JSON object shape: event
    /// URI to event payload. A conformant logout token carries the back-channel logout
    /// event with an empty-object payload.
    /// </summary>
    public IDictionary<string, IdentityClaimValue> Events { get; } =
        new Dictionary<string, IdentityClaimValue>(StringComparer.Ordinal);

    /// <summary>
    /// Gets the extension claims beyond the typed members (including a prohibited
    /// <c>nonce</c>, which <see cref="OpenIdConnectLogoutToken.Validate" /> reports).
    /// </summary>
    public IList<IIdentityClaim> AdditionalClaims { get; } = new List<IIdentityClaim>();

    /// <summary>
    /// Gets or sets the original compact serialization, when retained.
    /// </summary>
    public string? RawToken { get; set; }
}
