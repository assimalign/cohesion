using System;

namespace Assimalign.Cohesion.IdentityModel.Protocols;

/// <summary>
/// Represents the shared envelope of a protocol request message. Protocol branches derive
/// concrete request types (an OpenID Connect authorization request, a SAML authentication
/// request) that add their spec parameters.
/// </summary>
public abstract class ProtocolRequest : ProtocolMessage
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProtocolRequest" /> class by
    /// snapshotting the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The request contents.</param>
    /// <param name="protocol">The protocol the derived request type models.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a property name is blank or a property value is undefined.
    /// </exception>
    protected ProtocolRequest(ProtocolRequestDescriptor descriptor, AuthenticationProtocol protocol)
        : base(descriptor, protocol)
    {
    }
}
