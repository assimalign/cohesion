namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Defines the SAML 2.0 subject confirmation method URIs (SAML Profiles §3).
/// </summary>
public static class SamlConfirmationMethods
{
    /// <summary>The bearer confirmation method (the Web Browser SSO profile method).</summary>
    public const string Bearer = "urn:oasis:names:tc:SAML:2.0:cm:bearer";

    /// <summary>The holder-of-key confirmation method.</summary>
    public const string HolderOfKey = "urn:oasis:names:tc:SAML:2.0:cm:holder-of-key";

    /// <summary>The sender-vouches confirmation method.</summary>
    public const string SenderVouches = "urn:oasis:names:tc:SAML:2.0:cm:sender-vouches";
}
