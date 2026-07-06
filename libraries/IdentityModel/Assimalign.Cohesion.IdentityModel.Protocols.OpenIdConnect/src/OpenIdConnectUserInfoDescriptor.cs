using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Describes the contents of a UserInfo response before it is materialized into an
/// immutable <see cref="OpenIdConnectUserInfo" />.
/// </summary>
public class OpenIdConnectUserInfoDescriptor
{
    /// <summary>
    /// Gets or sets the subject (<c>sub</c>), as the raw wire string.
    /// </summary>
    public string? Subject { get; set; }

    /// <summary>
    /// Gets or sets the issuer the response was obtained from, used as the claims'
    /// asserting party. Not a wire member of the UserInfo document itself.
    /// </summary>
    public string? Issuer { get; set; }

    /// <summary>
    /// Gets the claims the response asserts, beyond the subject.
    /// </summary>
    public IList<IIdentityClaim> AdditionalClaims { get; } = new List<IIdentityClaim>();

    /// <summary>
    /// Gets the unresolved aggregated and distributed claims references.
    /// </summary>
    public IList<OpenIdConnectClaimsSource> ClaimsSources { get; } = new List<OpenIdConnectClaimsSource>();

    /// <summary>
    /// Gets or sets the as-received response document (JSON text, or the compact JWT for
    /// signed responses), preserved for provenance. Null when not retained.
    /// </summary>
    public string? RawDocument { get; set; }
}
