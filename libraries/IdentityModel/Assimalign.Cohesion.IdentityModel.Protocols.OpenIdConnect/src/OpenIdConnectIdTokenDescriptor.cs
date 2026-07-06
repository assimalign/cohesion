using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Describes the claim surface of an ID token before it is materialized into an immutable
/// <see cref="OpenIdConnectIdToken" />. The typed members are the single input for the
/// claims they name; <see cref="AdditionalClaims" /> carries extension claims and must not
/// collide with a typed member's claim name.
/// </summary>
public class OpenIdConnectIdTokenDescriptor
{
    /// <summary>
    /// Gets or sets the issuer (<c>iss</c>).
    /// </summary>
    public string? Issuer { get; set; }

    /// <summary>
    /// Gets or sets the subject (<c>sub</c>), as the raw wire string. Canonical lift
    /// happens once, via the shared <c>GetSubjectIdentifier</c> extension, so login and
    /// logout legs derive identical identifiers.
    /// </summary>
    public string? Subject { get; set; }

    /// <summary>
    /// Gets the audiences (<c>aud</c>).
    /// </summary>
    public IList<string> Audiences { get; } = new List<string>();

    /// <summary>
    /// Gets or sets the expiration instant (<c>exp</c>).
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// Gets or sets the issuance instant (<c>iat</c>).
    /// </summary>
    public DateTimeOffset? IssuedAt { get; set; }

    /// <summary>
    /// Gets or sets the not-before instant (<c>nbf</c>).
    /// </summary>
    public DateTimeOffset? NotBefore { get; set; }

    /// <summary>
    /// Gets or sets the authentication instant (<c>auth_time</c>).
    /// </summary>
    public DateTimeOffset? AuthTime { get; set; }

    /// <summary>
    /// Gets or sets the replay-prevention nonce (<c>nonce</c>).
    /// </summary>
    public string? Nonce { get; set; }

    /// <summary>
    /// Gets or sets the authentication context class reference (<c>acr</c>).
    /// </summary>
    public string? Acr { get; set; }

    /// <summary>
    /// Gets the authentication method references (<c>amr</c>).
    /// </summary>
    public IList<string> Amr { get; } = new List<string>();

    /// <summary>
    /// Gets or sets the authorized party (<c>azp</c>).
    /// </summary>
    public string? Azp { get; set; }

    /// <summary>
    /// Gets or sets the access token hash (<c>at_hash</c>).
    /// </summary>
    public string? AccessTokenHash { get; set; }

    /// <summary>
    /// Gets or sets the code hash (<c>c_hash</c>).
    /// </summary>
    public string? CodeHash { get; set; }

    /// <summary>
    /// Gets or sets the provider session identifier (<c>sid</c>).
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// Gets or sets the token identifier (<c>jti</c>), the value that feeds
    /// <c>AuthenticationResult.EvidenceId</c>.
    /// </summary>
    public string? JwtId { get; set; }

    /// <summary>
    /// Gets or sets the original compact serialization, preserved so later flows (an
    /// RP-initiated logout's <c>id_token_hint</c>, signature re-verification) need no side
    /// channel. Null when not retained.
    /// </summary>
    public string? RawToken { get; set; }

    /// <summary>
    /// Gets the unresolved aggregated and distributed claims references.
    /// </summary>
    public IList<OpenIdConnectClaimsSource> ClaimsSources { get; } = new List<OpenIdConnectClaimsSource>();

    /// <summary>
    /// Gets the extension claims beyond the typed members. Entries whose type collides
    /// with a typed member's claim name are rejected at materialization.
    /// </summary>
    public IList<IIdentityClaim> AdditionalClaims { get; } = new List<IIdentityClaim>();
}
