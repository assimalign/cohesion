using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols;

/// <summary>
/// Describes the shared envelope of a protocol message before it is materialized into a
/// <see cref="ProtocolMessage" /> derivative. Abstract because
/// <see cref="ProtocolMessage" /> is abstract: protocol branches pair each concrete
/// message type with its own descriptor derivative.
/// </summary>
public abstract class ProtocolMessageDescriptor
{
    /// <summary>
    /// Gets or sets the message identifier (a SAML <c>ID</c>). Null for protocols whose
    /// messages carry no identifier (OpenID Connect).
    /// </summary>
    public string? MessageId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the party that <em>sent</em> the message (a SAML
    /// <c>Issuer</c>; the client identifier on OpenID Connect requests, the issuer on
    /// OpenID Connect responses).
    /// </summary>
    public string? Issuer { get; set; }

    /// <summary>
    /// Gets or sets the address the message was sent to, as the exact wire string (a SAML
    /// <c>Destination</c>). Deliberately not validated at materialization: this is wire
    /// capture, and judging it is a validator's job.
    /// </summary>
    public string? Destination { get; set; }

    /// <summary>
    /// Gets or sets the instant the message was issued (a SAML <c>IssueInstant</c>).
    /// </summary>
    public DateTimeOffset? IssuedAt { get; set; }

    /// <summary>
    /// Gets or sets the opaque correlation value that rides alongside the message on both
    /// legs of an exchange (OpenID Connect <c>state</c> / SAML <c>RelayState</c>).
    /// </summary>
    public string? CorrelationState { get; set; }

    /// <summary>
    /// Gets or sets the as-transmitted textual form of the message.
    /// </summary>
    public string? RawMessage { get; set; }

    /// <summary>
    /// Gets additional message detail.
    /// </summary>
    public IDictionary<string, IdentityClaimValue> Properties { get; } =
        new Dictionary<string, IdentityClaimValue>(StringComparer.Ordinal);
}
