namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Defines common SAML 2.0 authentication context class URIs (SAML AuthnContext).
/// </summary>
public static class SamlAuthnContextClasses
{
    /// <summary>Password authentication over a protected transport.</summary>
    public const string PasswordProtectedTransport = "urn:oasis:names:tc:SAML:2.0:ac:classes:PasswordProtectedTransport";

    /// <summary>Password authentication.</summary>
    public const string Password = "urn:oasis:names:tc:SAML:2.0:ac:classes:Password";

    /// <summary>Kerberos authentication.</summary>
    public const string Kerberos = "urn:oasis:names:tc:SAML:2.0:ac:classes:Kerberos";

    /// <summary>X.509 certificate authentication.</summary>
    public const string X509 = "urn:oasis:names:tc:SAML:2.0:ac:classes:X509";

    /// <summary>Smartcard authentication.</summary>
    public const string Smartcard = "urn:oasis:names:tc:SAML:2.0:ac:classes:Smartcard";

    /// <summary>Smartcard PKI authentication.</summary>
    public const string SmartcardPki = "urn:oasis:names:tc:SAML:2.0:ac:classes:SmartcardPKI";

    /// <summary>TLS client certificate authentication.</summary>
    public const string TlsClient = "urn:oasis:names:tc:SAML:2.0:ac:classes:TLSClient";

    /// <summary>Unspecified authentication.</summary>
    public const string Unspecified = "urn:oasis:names:tc:SAML:2.0:ac:classes:unspecified";
}
