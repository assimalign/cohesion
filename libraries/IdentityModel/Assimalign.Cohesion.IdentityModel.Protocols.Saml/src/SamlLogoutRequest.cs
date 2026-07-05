using System;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Represents a SAML 2.0 <c>LogoutRequest</c> (SAML Core §3.7.1). Single-logout
/// orchestration correlates it against stored sessions on the inherited
/// (<see cref="ProtocolMessage.Issuer" />, <see cref="ProtocolLogoutRequest.ProviderSessionIds" />)
/// pair — identically to OpenID Connect back-channel logout — where the session identifiers
/// are the request's <c>SessionIndex</c> values and a session matches when any of its
/// provider session identifiers is among them (an empty set meaning every session for the
/// subject).
/// </summary>
public sealed class SamlLogoutRequest : ProtocolLogoutRequest
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SamlLogoutRequest" /> class by
    /// snapshotting the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The request contents.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a session identifier entry is null or whitespace, or a property name is
    /// blank or a property value is undefined.
    /// </exception>
    /// <exception cref="IdentityModelException">Thrown when the descriptor has no message identifier.</exception>
    public SamlLogoutRequest(SamlLogoutRequestDescriptor descriptor)
        : base(descriptor, AuthenticationProtocol.Saml2)
    {
        if (string.IsNullOrWhiteSpace(descriptor.MessageId))
        {
            throw new IdentityModelException("A SAML logout request requires a message identifier.");
        }

        NameId = descriptor.NameId;
    }

    /// <summary>
    /// Gets the wire-faithful NameID identifying the principal to log out.
    /// </summary>
    public SamlNameId? NameId { get; }
}
