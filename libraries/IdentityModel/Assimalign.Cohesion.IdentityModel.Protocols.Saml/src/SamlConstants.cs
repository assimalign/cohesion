namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Defines the core SAML 2.0 constant values: version, XML namespace URIs, and consent
/// identifiers.
/// </summary>
public static class SamlConstants
{
    /// <summary>
    /// The SAML 2.0 version string (<c>2.0</c>).
    /// </summary>
    public const string Version = "2.0";

    /// <summary>
    /// The SAML 2.0 assertion XML namespace.
    /// </summary>
    public const string AssertionNamespace = "urn:oasis:names:tc:SAML:2.0:assertion";

    /// <summary>
    /// The SAML 2.0 protocol XML namespace.
    /// </summary>
    public const string ProtocolNamespace = "urn:oasis:names:tc:SAML:2.0:protocol";

    /// <summary>
    /// The SAML 2.0 metadata XML namespace.
    /// </summary>
    public const string MetadataNamespace = "urn:oasis:names:tc:SAML:2.0:metadata";

    /// <summary>
    /// The consent value indicating consent is unspecified.
    /// </summary>
    public const string ConsentUnspecified = "urn:oasis:names:tc:SAML:2.0:consent:unspecified";

    /// <summary>
    /// The consent value indicating consent was obtained.
    /// </summary>
    public const string ConsentObtained = "urn:oasis:names:tc:SAML:2.0:consent:obtained";
}
