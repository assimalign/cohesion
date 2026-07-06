using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols;

/// <summary>
/// Represents the shared semantics of a logout request across protocols. Single-logout
/// orchestration correlates a logout request against stored sessions using
/// (<see cref="ProtocolMessage.Issuer" />, <see cref="ProviderSessionIds" />) — the same
/// pair <c>AuthenticationSession</c> stores — identically for SAML single logout and
/// OpenID Connect back-channel logout; the divergent wire shapes live on branch-derived
/// types.
/// </summary>
public abstract class ProtocolLogoutRequest : ProtocolRequest
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProtocolLogoutRequest" /> class by
    /// snapshotting the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The logout request contents.</param>
    /// <param name="protocol">The protocol the derived request type models.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a session identifier entry is null or whitespace, or when a property
    /// name is blank or a property value is undefined.
    /// </exception>
    protected ProtocolLogoutRequest(ProtocolLogoutRequestDescriptor descriptor, AuthenticationProtocol protocol)
        : base(descriptor, protocol)
    {
        Subject = descriptor.Subject;
        ProviderSessionIds = ModelSnapshot.Strings(descriptor.ProviderSessionIds, nameof(descriptor));
        Reason = descriptor.Reason;
        NotOnOrAfter = descriptor.NotOnOrAfter;
    }

    /// <summary>
    /// Gets the identifier of the subject to log out, when resolvable.
    /// </summary>
    public SubjectIdentifier? Subject { get; }

    /// <summary>
    /// Gets the provider session identifiers the logout applies to. An empty list with a
    /// non-null <see cref="Subject" /> means every session for that principal (SAML
    /// no-<c>SessionIndex</c> semantics).
    /// </summary>
    public IReadOnlyList<string> ProviderSessionIds { get; }

    /// <summary>
    /// Gets why the logout is happening (a SAML <c>Reason</c> URI); never a subject hint.
    /// </summary>
    public string? Reason { get; }

    /// <summary>
    /// Gets the instant at or after which the logout request must not be honored (SAML
    /// <c>NotOnOrAfter</c>; the named instant itself is outside the request's validity).
    /// </summary>
    public DateTimeOffset? NotOnOrAfter { get; }
}
