using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Represents an immutable snapshot of an authentication session: an authenticated subject's
/// sign-in state at a service, correlated to the asserting provider's own session so that
/// single logout can be honored.
/// </summary>
/// <remarks>
/// The session deliberately holds the subject's <see cref="SubjectIdentifier" /> rather than
/// a full subject graph: session stores persist and rehydrate sessions, correlation and
/// logout need only the identifier, and claims snapshotted into a session would silently go
/// stale over its lifetime. Session <em>management</em> — state transitions, persistence —
/// is a service concern (IdentityHub); this model is the descriptive contract those services
/// share.
/// </remarks>
public sealed class AuthenticationSession
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AuthenticationSession" /> class by
    /// snapshotting the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The session contents.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a provider session identifier entry is null or whitespace, or when a
    /// property name is blank or a property value is undefined.
    /// </exception>
    /// <exception cref="IdentityModelException">
    /// Thrown when the descriptor has no session identifier, no subject, or no creation
    /// instant.
    /// </exception>
    /// <remarks>
    /// An expiry at or before the creation instant is deliberately legal: wire-sourced
    /// instants (for example a SAML <c>SessionNotOnOrAfter</c>) come from the provider's
    /// clock and may precede the locally observed completion instant under skew. Such a
    /// session materializes normally and <see cref="IsActive" /> reports it inactive —
    /// temporal judgment lives in one place.
    /// </remarks>
    public AuthenticationSession(AuthenticationSessionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (string.IsNullOrWhiteSpace(descriptor.SessionId))
        {
            throw new IdentityModelException("An authentication session requires a session identifier.");
        }

        if (descriptor.Subject is null)
        {
            throw new IdentityModelException("An authentication session requires a subject identifier.");
        }

        if (descriptor.CreatedAt is null)
        {
            throw new IdentityModelException("An authentication session requires a creation instant.");
        }

        SessionId = descriptor.SessionId;
        Subject = descriptor.Subject;
        SubjectKind = descriptor.SubjectKind;
        Protocol = descriptor.Protocol;
        Issuer = descriptor.Issuer;
        ProviderSessionIds = ModelSnapshot.Strings(descriptor.ProviderSessionIds, nameof(descriptor));
        Context = descriptor.Context;
        CreatedAt = descriptor.CreatedAt.Value;
        ExpiresAt = descriptor.ExpiresAt;
        State = descriptor.State;
        Properties = ModelSnapshot.Properties(descriptor.Properties, nameof(descriptor));
    }

    /// <summary>
    /// Gets the session identifier.
    /// </summary>
    public string SessionId { get; }

    /// <summary>
    /// Gets the identifier of the authenticated subject.
    /// </summary>
    public SubjectIdentifier Subject { get; }

    /// <summary>
    /// Gets the kind of the authenticated subject.
    /// </summary>
    public IdentityKind SubjectKind { get; }

    /// <summary>
    /// Gets the protocol that authenticated the session.
    /// </summary>
    public AuthenticationProtocol Protocol { get; }

    /// <summary>
    /// Gets the asserting provider (SAML IdP entity ID / OIDC issuer). Together with
    /// <see cref="ProviderSessionIds" /> this is what single logout correlates on:
    /// provider session identifiers are only unique per issuer.
    /// </summary>
    public string? Issuer { get; }

    /// <summary>
    /// Gets the provider session correlation identifiers (OIDC <c>sid</c> / SAML
    /// <c>SessionIndex</c> values). SAML permits several per session.
    /// </summary>
    public IReadOnlyList<string> ProviderSessionIds { get; }

    /// <summary>
    /// Gets the authentication context the session was established with.
    /// </summary>
    public AuthenticationContext? Context { get; }

    /// <summary>
    /// Gets the instant the session was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Gets the instant the session expires, when bounded.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>
    /// Gets the administrative state.
    /// </summary>
    public AuthenticationSessionState State { get; }

    /// <summary>
    /// Gets additional session data (for example a tenant scope or correlation
    /// identifiers).
    /// </summary>
    public IReadOnlyDictionary<string, IdentityClaimValue> Properties { get; }

    /// <summary>
    /// Determines whether the session is active at the provided instant: the state is
    /// <see cref="AuthenticationSessionState.Active" /> and the instant falls within the
    /// session window.
    /// </summary>
    /// <param name="at">The instant to evaluate.</param>
    /// <returns><see langword="true" /> when the session is active; otherwise <see langword="false" />.</returns>
    public bool IsActive(DateTimeOffset at)
    {
        return State == AuthenticationSessionState.Active
            && at >= CreatedAt
            && (ExpiresAt is null || at < ExpiresAt);
    }

    /// <inheritdoc />
    public override string ToString() => $"{SessionId} ({Subject.Value}, {State})";
}
