using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Carries the expectations an ID token's data rules are validated against. The
/// validation instant is a required constructor argument — a descriptive library never
/// owns a clock.
/// </summary>
public sealed class OpenIdConnectIdTokenValidationOptions
{
    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="OpenIdConnectIdTokenValidationOptions" /> class.
    /// </summary>
    /// <param name="validateAt">The instant to evaluate temporal rules at.</param>
    public OpenIdConnectIdTokenValidationOptions(DateTimeOffset validateAt)
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
    /// Gets or sets the client identifier the token's audiences must include. Null skips
    /// the audience checks.
    /// </summary>
    public string? ExpectedAudience { get; set; }

    /// <summary>
    /// Gets the additional audiences the client trusts beyond
    /// <see cref="ExpectedAudience" />. Additional audiences outside this set are errors
    /// by default — the Core §3.1.3.7 posture — unless
    /// <see cref="AllowAdditionalAudiences" /> opts out.
    /// </summary>
    public IList<string> TrustedAudiences { get; } = new List<string>();

    /// <summary>
    /// Gets or sets whether additional audiences outside the trusted set are accepted.
    /// Defaults to <see langword="false" /> (fail closed).
    /// </summary>
    public bool AllowAdditionalAudiences { get; set; }

    /// <summary>
    /// Gets or sets the nonce the authentication request sent. Null skips the check.
    /// </summary>
    public string? ExpectedNonce { get; set; }

    /// <summary>
    /// Gets or sets the maximum authentication age in seconds the request asked for.
    /// When set, <c>auth_time</c> is required and its age is enforced.
    /// </summary>
    public long? MaxAge { get; set; }

    /// <summary>
    /// Gets or sets whether <c>auth_time</c> is required regardless of
    /// <see cref="MaxAge" /> (a client registered with <c>require_auth_time</c>).
    /// </summary>
    public bool RequireAuthTime { get; set; }
}
