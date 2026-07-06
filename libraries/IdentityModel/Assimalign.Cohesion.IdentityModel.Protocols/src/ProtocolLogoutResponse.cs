using System;

namespace Assimalign.Cohesion.IdentityModel.Protocols;

/// <summary>
/// Represents the shared semantics of a logout response across protocols. The outcome —
/// including partial results such as SAML's success-with-<c>PartialLogout</c> — is carried
/// by the inherited <see cref="ProtocolResponse.Status" />.
/// </summary>
public abstract class ProtocolLogoutResponse : ProtocolResponse
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProtocolLogoutResponse" /> class by
    /// snapshotting the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The logout response contents.</param>
    /// <param name="protocol">The protocol the derived response type models.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a property name is blank or a property value is undefined.
    /// </exception>
    /// <exception cref="IdentityModelException">Thrown when the descriptor has no status.</exception>
    protected ProtocolLogoutResponse(ProtocolLogoutResponseDescriptor descriptor, AuthenticationProtocol protocol)
        : base(descriptor, protocol)
    {
    }
}
