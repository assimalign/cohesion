using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Describes the contents of an authentication context before it is materialized into an
/// immutable <see cref="AuthenticationContext" />.
/// </summary>
public class AuthenticationContextDescriptor
{
    /// <summary>
    /// Gets or sets the instant the subject authenticated (OIDC <c>auth_time</c> / SAML
    /// <c>AuthnInstant</c>).
    /// </summary>
    public DateTimeOffset? AuthenticatedAt { get; set; }

    /// <summary>
    /// Gets or sets the authentication context class reference (OIDC <c>acr</c> / SAML
    /// <c>AuthnContextClassRef</c>).
    /// </summary>
    public string? ContextClass { get; set; }

    /// <summary>
    /// Gets the authentication method references (OIDC <c>amr</c>).
    /// </summary>
    public IList<string> Methods { get; } = new List<string>();

    /// <summary>
    /// Gets the authenticating authorities, in order (SAML <c>AuthenticatingAuthority</c>,
    /// for proxied authentication).
    /// </summary>
    public IList<string> AuthenticatingAuthorities { get; } = new List<string>();

    /// <summary>
    /// Gets the provider session correlation identifiers (OIDC <c>sid</c> / SAML
    /// <c>SessionIndex</c> values). SAML permits several per authentication.
    /// </summary>
    public IList<string> ProviderSessionIds { get; } = new List<string>();

    /// <summary>
    /// Gets or sets the provider-mandated session expiry (SAML <c>SessionNotOnOrAfter</c>).
    /// </summary>
    public DateTimeOffset? SessionExpiresAt { get; set; }

    /// <summary>
    /// Gets additional context data (for example a SAML <c>SubjectLocality</c>).
    /// </summary>
    public IDictionary<string, IdentityClaimValue> Properties { get; } =
        new Dictionary<string, IdentityClaimValue>(StringComparer.Ordinal);
}
