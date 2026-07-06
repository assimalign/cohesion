using System;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Represents a SAML 2.0 <c>LogoutResponse</c> (SAML Core §3.7.2). The outcome — including a
/// partial single-logout result — is carried by the inherited
/// <see cref="ProtocolResponse.Status" />.
/// </summary>
public sealed class SamlLogoutResponse : ProtocolLogoutResponse
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SamlLogoutResponse" /> class by
    /// snapshotting the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The response contents.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a property name is blank or a property value is undefined.
    /// </exception>
    /// <exception cref="IdentityModelException">Thrown when the descriptor has no status.</exception>
    public SamlLogoutResponse(SamlLogoutResponseDescriptor descriptor)
        : base(descriptor, AuthenticationProtocol.Saml2)
    {
    }
}
