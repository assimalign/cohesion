using System;

namespace Assimalign.Cohesion.IdentityModel.Token;

/// <summary>
/// Carries the expectations an identity token's protocol-neutral data rules are validated
/// against. The validation instant is a required constructor argument — a descriptive library
/// never owns a clock.
/// </summary>
public sealed class IdentityTokenValidationOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="IdentityTokenValidationOptions" /> class.
    /// </summary>
    /// <param name="validateAt">The instant to evaluate temporal rules at.</param>
    public IdentityTokenValidationOptions(DateTimeOffset validateAt)
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
    /// Gets or sets the issuer the token is expected from. Null skips the issuer check.
    /// </summary>
    public string? ExpectedIssuer { get; set; }

    /// <summary>
    /// Gets or sets the audience the token must be intended for. Null skips the audience
    /// check.
    /// </summary>
    public string? ExpectedAudience { get; set; }
}
