using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Describes the contents of a credential before it is materialized into an immutable
/// <see cref="IdentityCredential" />.
/// </summary>
public class IdentityCredentialDescriptor
{
    /// <summary>
    /// Gets or sets the credential identifier (for example a key identifier, certificate
    /// thumbprint, or passkey credential id). Required at materialization.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the credential kind.
    /// </summary>
    public IdentityCredentialKind Kind { get; set; }

    /// <summary>
    /// Gets or sets the administrative state. Defaults to
    /// <see cref="IdentityCredentialState.Unknown" />, which is never usable.
    /// </summary>
    public IdentityCredentialState State { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the subject the credential belongs to.
    /// </summary>
    public SubjectIdentifier? Subject { get; set; }

    /// <summary>
    /// Gets or sets the instant the credential was created or enrolled.
    /// </summary>
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the instant the credential becomes valid (for example an X.509
    /// <c>notBefore</c>).
    /// </summary>
    public DateTimeOffset? NotBefore { get; set; }

    /// <summary>
    /// Gets or sets the instant the credential expires.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// Gets additional credential metadata. Credential models carry references and
    /// metadata only — never secret material.
    /// </summary>
    public IDictionary<string, IdentityClaimValue> Properties { get; } =
        new Dictionary<string, IdentityClaimValue>(StringComparer.Ordinal);
}
