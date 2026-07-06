using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Represents a canonical identity subject: the protocol-neutral shape of an authenticated
/// principal, whether it was asserted by OpenID Connect, SAML 2.0, or another protocol.
/// </summary>
public interface IIdentitySubject
{
    /// <summary>
    /// Gets the kind of principal. <see cref="IdentityKind.Unknown" /> is legitimate for
    /// subjects normalized from protocol data that declares no kind (for example actor
    /// entries).
    /// </summary>
    IdentityKind Kind { get; }

    /// <summary>
    /// Gets the primary subject identifier.
    /// </summary>
    SubjectIdentifier Identifier { get; }

    /// <summary>
    /// Gets every identifier known for the subject, in order. The primary identifier is
    /// always the first element, and the list contains no duplicates.
    /// </summary>
    IReadOnlyList<SubjectIdentifier> Identifiers { get; }

    /// <summary>
    /// Gets the human-readable display name, when known.
    /// </summary>
    string? DisplayName { get; }

    /// <summary>
    /// Gets the normalized claims asserted about the subject.
    /// </summary>
    IIdentityClaimCollection Claims { get; }

    /// <summary>
    /// Gets the party acting as this subject, when the authentication involved delegation
    /// or impersonation (for example an OAuth 2.0 token-exchange <c>act</c> claim or a SAML
    /// delegation entry).
    /// </summary>
    /// <remarks>
    /// The chain direction is normative and matches RFC 8693 <c>act</c> nesting:
    /// <see cref="Actor" /> is the current acting party (acting on behalf of this
    /// subject), and <c>Actor.Actor</c> is the <em>prior</em> actor — the party that
    /// previously acted for the same subject before delegation passed to the current
    /// actor. The deepest link is the least recent actor, and the history trail is
    /// informational. Chains must be finite; this library enforces a maximum depth when
    /// materializing a subject, and consumers walking chains from third-party
    /// implementations should bound their walks the same way. Facts about a delegation
    /// link (for example a SAML <c>DelegationInstant</c>) are represented as claims on the
    /// acting subject with appropriate provenance.
    /// </remarks>
    IIdentitySubject? Actor { get; }
}
