namespace Assimalign.Cohesion.IdentityModel.Token.Saml;

/// <summary>
/// Defines the SAML 2.0 subject confirmation method URIs the SAML token package consumes. Only
/// the bearer method is defined here — it is the one the token-substrate validation checks; the
/// full method set (holder-of-key, sender-vouches) is a protocol-branch concern.
/// </summary>
/// <remarks>
/// This is the token-branch document mirror of
/// <c>Assimalign.Cohesion.IdentityModel.Protocols.Saml.SamlConfirmationMethods.Bearer</c>; the
/// token branch cannot reference the protocol branch, so it owns its own copy. A root-tests
/// drift guard pins the value equal across the two branches. This mirror is warranted (unlike
/// the NameID formats) because the confirmation-method URI is SAML-protocol vocabulary the root
/// canonical model does not own.
/// </remarks>
public static class SamlConfirmationMethods
{
    /// <summary>The bearer confirmation method (the Web Browser SSO profile method).</summary>
    public const string Bearer = "urn:oasis:names:tc:SAML:2.0:cm:bearer";
}
