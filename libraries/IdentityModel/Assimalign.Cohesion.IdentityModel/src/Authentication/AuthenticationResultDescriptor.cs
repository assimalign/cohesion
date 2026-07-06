using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Describes the contents of an authentication result before it is materialized into an
/// immutable <see cref="AuthenticationResult" />. Exactly one of <see cref="Subject" />
/// (success) or <see cref="Failure" /> (failure) must be set.
/// </summary>
public class AuthenticationResultDescriptor
{
    /// <summary>
    /// Gets or sets the authenticated subject. Setting this makes the result successful.
    /// </summary>
    public IIdentitySubject? Subject { get; set; }

    /// <summary>
    /// Gets or sets the failure. Setting this makes the result failed.
    /// </summary>
    public AuthenticationFailure? Failure { get; set; }

    /// <summary>
    /// Gets or sets the identifier the failed attempt claimed to authenticate as, so
    /// failed-attempt audit trails and lockout counters have a subject to key on.
    /// </summary>
    public SubjectIdentifier? AttemptedSubject { get; set; }

    /// <summary>
    /// Gets or sets the protocol that produced the result.
    /// </summary>
    public AuthenticationProtocol Protocol { get; set; }

    /// <summary>
    /// Gets or sets the instant the authentication attempt completed. Required at
    /// materialization.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// Gets or sets the asserting party (OIDC issuer / SAML IdP entity ID).
    /// </summary>
    public string? Issuer { get; set; }

    /// <summary>
    /// Gets or sets the party the authentication was performed for (OIDC audience or
    /// client / SAML audience restriction).
    /// </summary>
    public string? Audience { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the credential used, linking the result to an
    /// <see cref="IdentityCredential" /> for auditing.
    /// </summary>
    public string? CredentialId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the evidencing token or assertion (JWT <c>jti</c> /
    /// SAML assertion ID) for replay tracing, without referencing token types from
    /// descendant packages.
    /// </summary>
    public string? EvidenceId { get; set; }

    /// <summary>
    /// Gets or sets the authentication context. May be present on failures too (for
    /// example when a first factor succeeded and a later step failed).
    /// </summary>
    public AuthenticationContext? Context { get; set; }

    /// <summary>
    /// Gets additional result data (for example correlation identifiers or
    /// deployment-specific risk signals).
    /// </summary>
    public IDictionary<string, IdentityClaimValue> Properties { get; } =
        new Dictionary<string, IdentityClaimValue>(StringComparer.Ordinal);
}
