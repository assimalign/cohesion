using System;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Carries the relying-party context a SAML assertion's data rules are validated against.
/// The validation instant is a required constructor argument — a descriptive library never
/// owns a clock.
/// </summary>
public sealed class SamlAssertionValidationOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SamlAssertionValidationOptions" /> class.
    /// </summary>
    /// <param name="validateAt">The instant to evaluate temporal rules at.</param>
    public SamlAssertionValidationOptions(DateTimeOffset validateAt)
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
    /// Gets or sets the relying party (service provider entity id) that must satisfy the
    /// audience restrictions. Null skips the audience check.
    /// </summary>
    public string? ExpectedAudience { get; set; }

    /// <summary>
    /// Gets or sets the assertion consumer URL a bearer confirmation's recipient must match.
    /// Null skips the recipient check.
    /// </summary>
    public string? ExpectedRecipient { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the authentication request this assertion answers. A
    /// bearer confirmation's <c>InResponseTo</c> must match it; leave null for
    /// identity-provider-initiated single sign-on, where the confirmation carries no
    /// <c>InResponseTo</c>.
    /// </summary>
    public string? ExpectedInResponseTo { get; set; }

    /// <summary>
    /// Gets or sets whether a bearer subject confirmation is required (the Web Browser SSO
    /// profile). Defaults to <see langword="true" />.
    /// </summary>
    public bool RequireBearerConfirmation { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the assertion must carry an authentication statement (the Web
    /// Browser SSO profile, SAML Profiles §4.1.4.2). Defaults to <see langword="true" />. Set
    /// to <see langword="false" /> for the attribute-only assertions SAML Core §2.7.2 permits,
    /// which legitimately carry no authentication statement.
    /// </summary>
    public bool RequireAuthnStatement { get; set; } = true;
}
