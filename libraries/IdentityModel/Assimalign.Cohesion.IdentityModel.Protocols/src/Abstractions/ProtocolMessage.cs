using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols;

/// <summary>
/// Represents the shared, transport-agnostic envelope of an authentication protocol
/// message. Protocol branches derive concrete message types (authorization requests,
/// SAML authentication requests, logout messages) that add their spec parameters on top
/// of this envelope; the envelope itself carries only what is genuinely shared.
/// </summary>
/// <remarks>
/// <para>
/// The base is data-only: get-only properties snapshotted from a descriptor, no virtual
/// or abstract behavior members. The protocol is pinned by the derived type — it is a
/// constructor argument supplied by the derivative, never descriptor data.
/// </para>
/// <para>
/// Correlation semantics are pinned here once for the whole family:
/// <see cref="CorrelationState" /> carries the opaque round-trip value on <em>both</em>
/// legs of an exchange (OpenID Connect <c>state</c>, SAML <c>RelayState</c>);
/// message-<em>identifier</em> correlation (<see cref="ProtocolResponse.InResponseTo" />
/// echoing a request's <see cref="MessageId" />) exists only for protocols with message
/// identifiers and is always null for OpenID Connect.
/// </para>
/// </remarks>
public abstract class ProtocolMessage
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProtocolMessage" /> class by
    /// snapshotting the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The message envelope contents.</param>
    /// <param name="protocol">The protocol the derived message type models. Supplied by the derivative, never by descriptor data.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a property name is blank or a property value is undefined.
    /// </exception>
    protected ProtocolMessage(ProtocolMessageDescriptor descriptor, AuthenticationProtocol protocol)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        Protocol = protocol;
        MessageId = descriptor.MessageId;
        Issuer = descriptor.Issuer;
        Destination = descriptor.Destination;
        IssuedAt = descriptor.IssuedAt;
        CorrelationState = descriptor.CorrelationState;
        RawMessage = descriptor.RawMessage;
        Properties = ModelSnapshot.Properties(descriptor.Properties, nameof(descriptor));
    }

    /// <summary>
    /// Gets the protocol the message belongs to.
    /// </summary>
    public AuthenticationProtocol Protocol { get; }

    /// <summary>
    /// Gets the message identifier (a SAML <c>ID</c>); null for protocols whose messages
    /// carry no identifier.
    /// </summary>
    public string? MessageId { get; }

    /// <summary>
    /// Gets the identifier of the party that <em>sent</em> the message.
    /// </summary>
    public string? Issuer { get; }

    /// <summary>
    /// Gets the address the message was sent to, as the exact wire string. Compare
    /// ordinally — destination checking is a signed-value control in SAML.
    /// </summary>
    public string? Destination { get; }

    /// <summary>
    /// Gets the instant the message was issued.
    /// </summary>
    public DateTimeOffset? IssuedAt { get; }

    /// <summary>
    /// Gets the opaque correlation value that rides alongside the message on both legs of
    /// an exchange (OpenID Connect <c>state</c> / SAML <c>RelayState</c>).
    /// </summary>
    public string? CorrelationState { get; }

    /// <summary>
    /// Gets the as-transmitted textual form of the message (query string, form body,
    /// XML, or compact token), preserved for provenance. Null when not retained.
    /// </summary>
    public string? RawMessage { get; }

    /// <summary>
    /// Gets additional message detail.
    /// </summary>
    public IReadOnlyDictionary<string, IdentityClaimValue> Properties { get; }
}
