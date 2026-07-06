using System;

namespace Assimalign.Cohesion.IdentityModel.Token.Saml;

/// <summary>
/// Carries the expectations a SAML token's document-substrate rules are validated against. The
/// validation instant is a required constructor argument — a descriptive library never owns a
/// clock. Standalone (not derived from the sealed neutral token options) so both option types
/// grow additively, mirroring the JSON Web Token package.
/// </summary>
/// <remarks>
/// The scope is the token substrate: the neutral issuer/audience/temporal checks plus the bearer
/// subject-confirmation-data window (freshness, and recipient / in-response-to equality when the
/// caller expects them). The full SAML Core / Web Browser SSO profile — including the
/// require-a-bearer-confirmation posture — is the protocol branch's <c>SamlAssertion.Validate</c>
/// concern.
/// </remarks>
public sealed class SamlTokenValidationOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SamlTokenValidationOptions" /> class.
    /// </summary>
    /// <param name="validateAt">The instant to evaluate temporal rules at.</param>
    public SamlTokenValidationOptions(DateTimeOffset validateAt)
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
    /// Gets or sets the issuer the assertion is expected from. Null skips the check.
    /// </summary>
    public string? ExpectedIssuer { get; set; }

    /// <summary>
    /// Gets or sets the relying party that must satisfy the assertion's audience restrictions
    /// (AND across restrictions, OR within one). Null skips the audience check.
    /// </summary>
    public string? ExpectedAudience { get; set; }

    /// <summary>
    /// Gets or sets the assertion consumer URL a bearer confirmation's recipient must match.
    /// Null skips the recipient check.
    /// </summary>
    public string? ExpectedRecipient { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the request the assertion answers. A bearer confirmation's
    /// <c>InResponseTo</c> must match it when set; null skips the check (identity-provider-initiated
    /// single sign-on).
    /// </summary>
    public string? ExpectedInResponseTo { get; set; }

    /// <summary>
    /// Gets or sets whether the token must carry a satisfied bearer subject confirmation.
    /// Defaults to <see langword="false" />: the token layer validates the freshness of any
    /// bearer confirmation present but does not impose the profile's require-a-bearer posture,
    /// which belongs to the protocol branch.
    /// </summary>
    public bool RequireBearerConfirmation { get; set; }
}
