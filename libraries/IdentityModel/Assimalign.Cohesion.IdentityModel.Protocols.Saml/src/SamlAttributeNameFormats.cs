namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Defines the SAML 2.0 attribute name format URIs (SAML Core §8.2).
/// </summary>
public static class SamlAttributeNameFormats
{
    /// <summary>The unspecified attribute name format.</summary>
    public const string Unspecified = "urn:oasis:names:tc:SAML:2.0:attrname-format:unspecified";

    /// <summary>The URI reference attribute name format.</summary>
    public const string Uri = "urn:oasis:names:tc:SAML:2.0:attrname-format:uri";

    /// <summary>The basic attribute name format.</summary>
    public const string Basic = "urn:oasis:names:tc:SAML:2.0:attrname-format:basic";
}
