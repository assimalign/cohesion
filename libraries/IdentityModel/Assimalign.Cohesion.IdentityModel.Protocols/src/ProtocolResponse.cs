using System;

namespace Assimalign.Cohesion.IdentityModel.Protocols;

/// <summary>
/// Represents the shared envelope of a protocol response message. Protocol branches derive
/// concrete response types (an OpenID Connect authorization response, a SAML response)
/// that add their spec parameters.
/// </summary>
/// <remarks>
/// <see cref="Status" /> is required and has no default: absence-means-success is a
/// wire-parsing rule that belongs in a branch materializer, never a model default — a
/// response whose status was never mapped must fail construction rather than read as
/// accepted.
/// </remarks>
public abstract class ProtocolResponse : ProtocolMessage
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProtocolResponse" /> class by
    /// snapshotting the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The response contents.</param>
    /// <param name="protocol">The protocol the derived response type models.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a property name is blank or a property value is undefined.
    /// </exception>
    /// <exception cref="IdentityModelException">Thrown when the descriptor has no status.</exception>
    protected ProtocolResponse(ProtocolResponseDescriptor descriptor, AuthenticationProtocol protocol)
        : base(descriptor, protocol)
    {
        if (descriptor.Status is null)
        {
            throw new IdentityModelException(
                "A protocol response requires a status. Set ProtocolResponseStatus.Success explicitly for " +
                "protocols whose success responses carry no status element.");
        }

        InResponseTo = descriptor.InResponseTo;
        Status = descriptor.Status;
    }

    /// <summary>
    /// Gets the identifier of the request message this response answers (a SAML
    /// <c>InResponseTo</c>). Message-identifier correlation only — always null for
    /// protocols without message identifiers; see
    /// <see cref="ProtocolMessage.CorrelationState" /> for the opaque round-trip value.
    /// </summary>
    public string? InResponseTo { get; }

    /// <summary>
    /// Gets the response outcome.
    /// </summary>
    public ProtocolResponseStatus Status { get; }
}
