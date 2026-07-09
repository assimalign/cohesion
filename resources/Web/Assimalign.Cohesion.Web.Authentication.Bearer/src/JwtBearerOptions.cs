using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Web.Authentication.Bearer;

/// <summary>
/// Configures a JWT bearer authentication scheme: the accepted issuers and audiences, the
/// signing keys, the allowed algorithms and clock skew, and how validated claims map onto the
/// <see cref="System.Security.Claims.ClaimsPrincipal"/>.
/// </summary>
/// <remarks>
/// The handler consumes the IdentityModel JSON Web Token validation contracts for the
/// document-level rules and this type's <see cref="SigningKeys"/> for signature verification, so
/// it never embeds its own crypto policy beyond selecting a BCL primitive.
/// </remarks>
public sealed class JwtBearerOptions
{
    /// <summary>
    /// Gets or sets the scheme's human-readable display name.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets the issuers the token's <c>iss</c> must match (any-of). Empty skips issuer validation.
    /// </summary>
    public IList<string> ValidIssuers { get; } = new List<string>();

    /// <summary>
    /// Gets the audiences the token's <c>aud</c> must include (any-of). Empty skips audience
    /// validation.
    /// </summary>
    public IList<string> ValidAudiences { get; } = new List<string>();

    /// <summary>
    /// Gets the algorithms the token's <c>alg</c> must be among. Empty accepts any algorithm one
    /// of the <see cref="SigningKeys"/> can verify (the unsecured <c>none</c> algorithm is always
    /// rejected).
    /// </summary>
    public IList<string> AllowedAlgorithms { get; } = new List<string>();

    /// <summary>
    /// Gets the keys used to verify token signatures. At least one is required unless
    /// <see cref="RequireSignedTokens"/> is <see langword="false"/>.
    /// </summary>
    public IList<IJwtSignatureVerifier> SigningKeys { get; } = new List<IJwtSignatureVerifier>();

    /// <summary>
    /// Gets or sets the permitted clock skew for the temporal (<c>exp</c>/<c>nbf</c>) checks.
    /// Defaults to five minutes.
    /// </summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets whether a signature must be present and valid. Defaults to
    /// <see langword="true"/>; leave it so in production.
    /// </summary>
    public bool RequireSignedTokens { get; set; } = true;

    /// <summary>
    /// Gets or sets the claim type mapped to <see cref="System.Security.Claims.ClaimsIdentity.Name"/>.
    /// Defaults to <c>name</c>; set to <c>sub</c> for tokens that carry no display name.
    /// </summary>
    public string NameClaimType { get; set; } = "name";

    /// <summary>
    /// Gets or sets the claim type used for role checks
    /// (<see cref="System.Security.Claims.ClaimsPrincipal.IsInRole(string)"/>). Defaults to
    /// <c>roles</c> (RFC 9068).
    /// </summary>
    public string RoleClaimType { get; set; } = "roles";

    /// <summary>
    /// Gets or sets the realm advertised in the <c>WWW-Authenticate: Bearer</c> challenge, or
    /// <see langword="null"/> to omit the realm parameter.
    /// </summary>
    public string? Realm { get; set; }

    /// <summary>
    /// Gets or sets the issuer stamped on mapped claims when a source claim carries none. Defaults
    /// to <see langword="null"/>, in which case the token's <c>iss</c> is used.
    /// </summary>
    public string? ClaimsIssuer { get; set; }

    /// <summary>
    /// Gets or sets the time source used for the temporal checks. Defaults to
    /// <see cref="TimeProvider.System"/>.
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}
