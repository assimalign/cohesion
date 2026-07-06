using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Represents how and when a subject authenticated: the assurance and context data carried
/// by an authentication event (OIDC <c>auth_time</c>/<c>acr</c>/<c>amr</c>/<c>sid</c>, SAML
/// authentication statement content). Authorization and auditing layers consume this to
/// reason about authentication strength and recency.
/// </summary>
public sealed class AuthenticationContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AuthenticationContext" /> class by
    /// snapshotting the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The context contents.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a method, authority, or session identifier entry is null or whitespace,
    /// or when a property value is undefined.
    /// </exception>
    public AuthenticationContext(AuthenticationContextDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        AuthenticatedAt = descriptor.AuthenticatedAt;
        ContextClass = descriptor.ContextClass;
        Methods = ModelSnapshot.Strings(descriptor.Methods, nameof(descriptor));
        AuthenticatingAuthorities = ModelSnapshot.Strings(descriptor.AuthenticatingAuthorities, nameof(descriptor));
        ProviderSessionIds = ModelSnapshot.Strings(descriptor.ProviderSessionIds, nameof(descriptor));
        SessionExpiresAt = descriptor.SessionExpiresAt;
        Properties = ModelSnapshot.Properties(descriptor.Properties, nameof(descriptor));
    }

    /// <summary>
    /// Gets the instant the subject authenticated (OIDC <c>auth_time</c> / SAML
    /// <c>AuthnInstant</c>).
    /// </summary>
    public DateTimeOffset? AuthenticatedAt { get; }

    /// <summary>
    /// Gets the authentication context class reference (OIDC <c>acr</c> / SAML
    /// <c>AuthnContextClassRef</c>).
    /// </summary>
    public string? ContextClass { get; }

    /// <summary>
    /// Gets the authentication method references (OIDC <c>amr</c>).
    /// </summary>
    public IReadOnlyList<string> Methods { get; }

    /// <summary>
    /// Gets the authenticating authorities, in order (SAML <c>AuthenticatingAuthority</c>,
    /// for proxied authentication).
    /// </summary>
    public IReadOnlyList<string> AuthenticatingAuthorities { get; }

    /// <summary>
    /// Gets the provider session correlation identifiers (OIDC <c>sid</c> / SAML
    /// <c>SessionIndex</c> values).
    /// </summary>
    public IReadOnlyList<string> ProviderSessionIds { get; }

    /// <summary>
    /// Gets the provider-mandated session expiry (SAML <c>SessionNotOnOrAfter</c>).
    /// </summary>
    public DateTimeOffset? SessionExpiresAt { get; }

    /// <summary>
    /// Gets additional context data (for example a SAML <c>SubjectLocality</c>).
    /// </summary>
    public IReadOnlyDictionary<string, IdentityClaimValue> Properties { get; }
}
