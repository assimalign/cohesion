namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Describes the contents of a SAML <c>LogoutResponse</c> before it is materialized into an
/// immutable <see cref="SamlLogoutResponse" />. A partial single-logout result is a success
/// with a <see cref="SamlStatusCodes.PartialLogout" /> sub-code on the inherited status.
/// </summary>
public class SamlLogoutResponseDescriptor : ProtocolLogoutResponseDescriptor
{
}
