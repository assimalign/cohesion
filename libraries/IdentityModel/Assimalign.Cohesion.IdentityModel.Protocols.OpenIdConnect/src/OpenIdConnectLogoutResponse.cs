using System;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Represents an RP-initiated logout response: the redirect back to the post-logout
/// redirect URI, carrying only the <c>state</c> echo on the inherited
/// <see cref="ProtocolMessage.CorrelationState" />. A statusless success redirect sets
/// <see cref="ProtocolResponseStatus.Success" /> explicitly.
/// </summary>
public sealed class OpenIdConnectLogoutResponse : ProtocolLogoutResponse
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OpenIdConnectLogoutResponse" /> class
    /// by snapshotting the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The response contents.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a property name is blank or a property value is undefined.
    /// </exception>
    /// <exception cref="IdentityModelException">Thrown when the descriptor has no status.</exception>
    public OpenIdConnectLogoutResponse(OpenIdConnectLogoutResponseDescriptor descriptor)
        : base(descriptor, AuthenticationProtocol.OpenIdConnect)
    {
    }
}
