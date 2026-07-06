using System;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Carries the expectations a logout token's data rules are validated against.
/// </summary>
public sealed class OpenIdConnectLogoutTokenValidationOptions
{
    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="OpenIdConnectLogoutTokenValidationOptions" /> class.
    /// </summary>
    /// <param name="validateAt">The instant to evaluate temporal rules at.</param>
    public OpenIdConnectLogoutTokenValidationOptions(DateTimeOffset validateAt)
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
    /// the check.
    /// </summary>
    public string? ExpectedAudience { get; set; }

    /// <summary>
    /// Gets the additional audiences the client trusts beyond
    /// <see cref="ExpectedAudience" />. Back-Channel Logout §2.6 validates audiences the
    /// same way ID tokens do: additional audiences outside this set are errors by
    /// default, unless <see cref="AllowAdditionalAudiences" /> opts out.
    /// </summary>
    public System.Collections.Generic.IList<string> TrustedAudiences { get; } =
        new System.Collections.Generic.List<string>();

    /// <summary>
    /// Gets or sets whether additional audiences outside the trusted set are accepted.
    /// Defaults to <see langword="false" /> (fail closed).
    /// </summary>
    public bool AllowAdditionalAudiences { get; set; }
}
