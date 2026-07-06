using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

/// <summary>
/// Carries the expectations a JSON Web Token's document-level rules are validated against. The
/// validation instant is a required constructor argument — a descriptive library never owns a
/// clock. Standalone (not derived from the neutral token options) so both option types grow
/// additively, mirroring the sibling <c>OpenIdConnectIdTokenValidationOptions</c>.
/// </summary>
/// <remarks>
/// The scope is deliberately document-level: algorithm and hash concerns, plus the neutral
/// issuer/audience/temporal checks the token base already owns. Protocol-profile rules — nonce
/// match, azp-equals-client, <c>max_age</c>, additional-audience trust posture — are the OpenID
/// Connect branch's concern and are not modeled here.
/// </remarks>
public sealed class JsonWebTokenValidationOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="JsonWebTokenValidationOptions" /> class.
    /// </summary>
    /// <param name="validateAt">The instant to evaluate temporal rules at.</param>
    public JsonWebTokenValidationOptions(DateTimeOffset validateAt)
    {
        ValidateAt = validateAt;
    }

    /// <summary>
    /// Gets the instant temporal rules are evaluated at.
    /// </summary>
    public DateTimeOffset ValidateAt { get; }

    /// <summary>
    /// Gets or sets the allowed clock skew. Defaults to five minutes.
    /// </summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the issuer the token is expected from. Null skips the check.
    /// </summary>
    public string? ExpectedIssuer { get; set; }

    /// <summary>
    /// Gets or sets the audience the token must be intended for. Null skips the check.
    /// </summary>
    public string? ExpectedAudience { get; set; }

    /// <summary>
    /// Gets or sets whether the unsecured <c>none</c> algorithm is accepted. Defaults to
    /// <see langword="false" />, so an unsecured token is rejected (RFC 8725 §3.2).
    /// </summary>
    public bool AllowUnsecured { get; set; }

    /// <summary>
    /// Gets the algorithms the token's <c>alg</c> must be among. Empty means any algorithm is
    /// accepted (except <c>none</c>, which <see cref="AllowUnsecured" /> governs).
    /// </summary>
    public IList<string> AllowedAlgorithms { get; } = new List<string>();

    /// <summary>
    /// Gets the claim names that must be present on the token. Empty imposes no
    /// required-claim rule (the document layer does not assume a protocol's required set).
    /// </summary>
    public IList<string> RequiredClaims { get; } = new List<string>();

    /// <summary>
    /// Gets or sets the access token whose hash the token's <c>at_hash</c> claim must match.
    /// Null skips the access-token hash check.
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>
    /// Gets or sets the authorization code whose hash the token's <c>c_hash</c> claim must
    /// match. Null skips the code hash check.
    /// </summary>
    public string? AuthorizationCode { get; set; }

    /// <summary>
    /// Gets or sets whether a missing hash claim is an error when the value to hash was
    /// supplied. Defaults to <see langword="false" /> (the missing claim is reported as a
    /// warning), so pure code-flow ID tokens — where <c>at_hash</c> is optional — stay valid.
    /// </summary>
    public bool RequireTokenHash { get; set; }

    /// <summary>
    /// Gets the critical header parameter names the caller understands and can honor. A
    /// <c>crit</c> entry outside this set is an error.
    /// </summary>
    public IList<string> KnownCriticalHeaders { get; } = new List<string>();
}
